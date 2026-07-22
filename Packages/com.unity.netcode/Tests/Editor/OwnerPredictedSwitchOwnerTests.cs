using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    [GhostComponent(SendTypeOptimization = GhostSendType.OnlyPredictedClients)]
    struct PredictedComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(SendTypeOptimization = GhostSendType.OnlyInterpolatedClients)]
    struct InterpolatedComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(SendTypeOptimization = GhostSendType.OnlyPredictedClients, OwnerSendType = SendToOwnerType.SendToOwner)]
    struct OwnedComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(SendTypeOptimization = GhostSendType.OnlyPredictedClients)]
    struct PredictedOnlyBuffer : IBufferElementData
    {
        [GhostField] public int Value;
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    internal partial class OwnerPredictedWriteSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            foreach (var (pred, inter, owned) in
                     SystemAPI.Query<
                        RefRW<PredictedComponentData>,
                        RefRW<InterpolatedComponentData>,
                        RefRW<OwnedComponentData>>())
            {
                pred.ValueRW.Value += 1;
                inter.ValueRW.Value += 10;
                owned.ValueRW.Value += 100;
            }
        }
    }

    internal class OwnerPredictedSwitchOwnerTests
    {
        [Test]
        public void SwitchingOwner_ChangeGhostModeOnClients()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 2);
                testWorld.Connect();
                testWorld.GoInGame();

                // 生成尚未分配所有者的 Ghost，客户端应按插值模式生成
                // AutoCommandTarget 应保持禁用
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner { NetworkId = -1 });
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                // 两个客户端都应生成插值 Ghost
                var clientGhosts = new Entity[2];
                for (var index = 0; index < testWorld.ClientWorlds.Length; index++)
                {
                    clientGhosts[index] =
                        testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[index]);
                    Assert.AreNotEqual(Entity.Null, clientGhosts[index]);
                    Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager
                        .HasComponent<PredictedGhost>(clientGhosts[index]));
                }

                for (var index = 0; index < testWorld.ClientWorlds.Length; index++)
                {
                    // 服务端切换所有者，但不启用 AutoCommandTarget
                    var owner = index;
                    var nonowner = (index + 1) % 2;
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt,
                        new GhostOwner { NetworkId = owner + 1 });
                    for (int i = 0; i < 8; ++i)
                        testWorld.Tick();
                    // 新所有者客户端应切换为预测模式，另一客户端保持插值模式
                    Assert.IsTrue(testWorld.ClientWorlds[index].EntityManager
                        .HasComponent<PredictedGhost>(clientGhosts[owner]));
                    Assert.IsFalse(testWorld.ClientWorlds[nonowner].EntityManager
                        .HasComponent<PredictedGhost>(clientGhosts[nonowner]));
                    // 释放所有权，验证客户端重新切换为插值模式
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt,
                        new GhostOwner { NetworkId = -1 });
                    for (int i = 0; i < 8; ++i)
                        testWorld.Tick();
                    // 两个客户端上的 Ghost 都应处于插值模式
                    Assert.IsFalse(testWorld.ClientWorlds[owner].EntityManager
                        .HasComponent<PredictedGhost>(clientGhosts[owner]));
                    Assert.IsFalse(testWorld.ClientWorlds[nonowner].EntityManager
                        .HasComponent<PredictedGhost>(clientGhosts[nonowner]));
                }
            }
        }

        [Test]
        public void SwitchingOwner_ServerReceiveCommandFromOwningClient([Values]GhostMode ghostMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = ghostMode;
                ghostGameObject.AddComponent<NetcodeTransformUsageFlagsTestAuthoring>();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new InputComponentDataConverter();
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                // 生成尚未分配所有者的 Ghost，客户端模式由 ghostMode 配置决定
                // AutoCommandTarget 初始保持禁用
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt,
                    new AutoCommandTarget { Enabled = false });
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner { NetworkId = -1 });
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                // 客户端应生成 Ghost，并符合当前配置模式
                var clientGhost = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientGhost);
                Assert.AreEqual(ghostMode == GhostMode.Predicted, testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhost>(clientGhost),
                    "We don't currently own this ghost.");
                // 服务端分配所有者并启用 AutoCommandTarget
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner { NetworkId = 1 });
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt,
                    new AutoCommandTarget { Enabled = true });
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                Assert.AreEqual(ghostMode == GhostMode.Predicted || ghostMode == GhostMode.OwnerPredicted, testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhost>(clientGhost),
                    "We currently own this ghost.");
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager
                    .GetComponentData<NetCode.AutoCommandTarget>(clientGhost).Enabled);
                var serverBuffer =
                    testWorld.ServerWorld.EntityManager.GetBuffer<InputBufferData<InputComponentData>>(serverEnt);
                var serverTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                Assert.NotZero(serverBuffer.Length,
                    "Server should have received commands from the client but the input command buffer is empty");
                Assert.IsTrue(serverBuffer.GetDataAtTick(serverTick, out var commandData));
                Assert.AreEqual(serverTick, commandData.Tick);
            }
        }

        class MixedComponentTypeConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddComponent<PredictedComponentData>(entity);
                baker.AddComponent<InterpolatedComponentData>(entity);
                baker.AddComponent<OwnedComponentData>(entity);
                var buffer = baker.AddBuffer<PredictedOnlyBuffer>(entity);
                // 客户端与服务端的 Buffer 初始数据相同
                // 所有权切换期间不应观察到长度变化
                buffer.Resize(8, NativeArrayOptions.ClearMemory);
                for (int i = 0; i < 8; ++i)
                    buffer.ElementAt(i).Value = (i+1) * 10;
            }
        }

        [Test]
        public void SwitchingOwnerDeserializeComponentCorreclty()
        {
            // 验证 OwnerPredicted Ghost 切换预测模式时
            // 仅插值和仅预测组件都能正确反序列化，并且不会产生错误
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(OwnerPredictedWriteSystem));
                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = false;
                ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new MixedComponentTypeConverter();
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                // 生成尚未分配所有者的 Ghost，客户端初始应使用插值模式
                // AutoCommandTarget 初始保持禁用
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                // 初始化组件数据，并由服务端 System 每 Tick 修改
                Assert.AreNotEqual(Entity.Null, serverEnt);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner { NetworkId = -1 });
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                // 客户端应生成插值 Ghost
                var clientGhost = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientGhost);
                Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhost>(clientGhost));
                // 该组件只发送给插值 Ghost，但字段本身未配置插值
                // 因此客户端值应对应服务端历史值，并落后当前插值延迟，默认延迟为 2
                {
                    var s1 = testWorld.ServerWorld.EntityManager.GetComponentData<InterpolatedComponentData>(serverEnt);
                    var s2 = testWorld.ServerWorld.EntityManager.GetComponentData<PredictedComponentData>(serverEnt);
                    var c1 = testWorld.ClientWorlds[0].EntityManager.GetComponentData<InterpolatedComponentData>(clientGhost);
                    var c2 = testWorld.ClientWorlds[0].EntityManager.GetComponentData<PredictedComponentData>(clientGhost);
                    Assert.AreEqual(80, s1.Value);
                    Assert.AreEqual(8, s2.Value);
                    Assert.AreEqual(50, c1.Value);
                    Assert.AreEqual(0, c2.Value, "The PredictedComponentData should have been never received, so its value should stay equals to 0");
                    var bs1 = testWorld.ServerWorld.EntityManager.GetBuffer<PredictedOnlyBuffer>(serverEnt);
                    var bc1 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<PredictedOnlyBuffer>(clientGhost);
                    Assert.AreEqual(8, bs1.Length);
                    Assert.AreEqual(8, bc1.Length);
                    for(int i=0;i<8;++i)
                        Assert.AreEqual(bs1[i].Value, bc1[i].Value);
                }
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner { NetworkId = 1 });
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhost>(clientGhost));
                {
                    var s1 = testWorld.ServerWorld.EntityManager.GetComponentData<InterpolatedComponentData>(serverEnt);
                    var s2 = testWorld.ServerWorld.EntityManager.GetComponentData<PredictedComponentData>(serverEnt);
                    var c1 = testWorld.ClientWorlds[0].EntityManager.GetComponentData<InterpolatedComponentData>(clientGhost);
                    var c2 = testWorld.ClientWorlds[0].EntityManager.GetComponentData<PredictedComponentData>(clientGhost);
                    Assert.AreEqual(160, s1.Value);
                    Assert.AreEqual(16, s2.Value);
                    Assert.AreEqual(50, c1.Value, "After switching to interpolated, the InterpolatedComponentData value should remain the same");
                    Assert.AreEqual(16, c2.Value, "After switching to interpolated, the PredictedComponentData should be in sync with the latest received server data");
                    var bs1 = testWorld.ServerWorld.EntityManager.GetBuffer<PredictedOnlyBuffer>(serverEnt);
                    var bc1 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<PredictedOnlyBuffer>(clientGhost);
                    Assert.AreEqual(8, bs1.Length);
                    Assert.AreEqual(8, bc1.Length);
                    for(int i=0;i<8;++i)
                        Assert.AreEqual(bs1[i].Value, bc1[i].Value);
                }

                // 释放所有权，再切回插值模式
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner { NetworkId = -1 });
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhost>(clientGhost));
                {
                    var s1 = testWorld.ServerWorld.EntityManager.GetComponentData<InterpolatedComponentData>(serverEnt);
                    var s2 = testWorld.ServerWorld.EntityManager.GetComponentData<PredictedComponentData>(serverEnt);
                    var c1 = testWorld.ClientWorlds[0].EntityManager.GetComponentData<InterpolatedComponentData>(clientGhost);
                    var c2 = testWorld.ClientWorlds[0].EntityManager.GetComponentData<PredictedComponentData>(clientGhost);
                    Assert.AreEqual(240, s1.Value);
                    Assert.AreEqual(24, s2.Value);
                    Assert.AreEqual(210, c1.Value,"After switching to interpolated, the InterpolatedComponentData value should have been updated");
                    Assert.AreEqual(16, c2.Value, "After switching to interpolated, the  PredictedComponentData should stay the same");
                    var bs1 = testWorld.ServerWorld.EntityManager.GetBuffer<PredictedOnlyBuffer>(serverEnt);
                    var bc1 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<PredictedOnlyBuffer>(clientGhost);
                    Assert.AreEqual(8, bs1.Length);
                    Assert.AreEqual(8, bc1.Length);
                    for(int i=0;i<8;++i)
                        Assert.AreEqual(bs1[i].Value, bc1[i].Value);
                }

                // 再次分配所有权，切回预测模式
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner { NetworkId = 1 });
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhost>(clientGhost));
                {
                    var s1 = testWorld.ServerWorld.EntityManager.GetComponentData<InterpolatedComponentData>(serverEnt);
                    var s2 = testWorld.ServerWorld.EntityManager.GetComponentData<PredictedComponentData>(serverEnt);
                    var c1 = testWorld.ClientWorlds[0].EntityManager.GetComponentData<InterpolatedComponentData>(clientGhost);
                    var c2 = testWorld.ClientWorlds[0].EntityManager.GetComponentData<PredictedComponentData>(clientGhost);
                    Assert.AreEqual(320, s1.Value);
                    Assert.AreEqual(32, s2.Value);
                    Assert.AreEqual(210, c1.Value, "After switching to interpolated, the InterpolatedComponentData value should remain the same");
                    Assert.AreEqual(32, c2.Value, "After switching to interpolated, the PredictedComponentData should be in sync with the latest received server data");
                    var bs1 = testWorld.ServerWorld.EntityManager.GetBuffer<PredictedOnlyBuffer>(serverEnt);
                    var bc1 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<PredictedOnlyBuffer>(clientGhost);
                    Assert.AreEqual(8, bs1.Length);
                    Assert.AreEqual(8, bc1.Length);
                    for(int i=0;i<8;++i)
                        Assert.AreEqual(bs1[i].Value, bc1[i].Value);
                }
            }
        }
    }
}
