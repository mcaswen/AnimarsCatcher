using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Networking.Transport;
using Unity.Transforms;
using Unity.Scenes;
using UnityEngine;
using Unity.NetCode.PrespawnTests;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.SceneManagement;
using System.Linq;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.NetCode.HostMigration;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class IncrementSomeDataSystem : SystemBase
    {
        private EntityQuery _someDataQuery;

        protected override void OnCreate()
        {
            _someDataQuery = GetEntityQuery(typeof(GhostInstance), typeof(SomeData));
        }

        protected override void OnUpdate()
        {
            var someDataEntites = _someDataQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < someDataEntites.Length; ++i)
            {
                EntityManager.SetComponentData(someDataEntites[i], new SomeData { Value = EntityManager.GetComponentData<SomeData>(someDataEntites[i]).Value + 1 });
            }
        }
    }

    [GhostComponent(PrefabType=GhostPrefabType.AllPredicted, OwnerSendType = SendToOwnerType.SendToNonOwner)]
    internal struct HMRemoteInput : IInputComponentData
    {
        [GhostField] public int Horizontal;
        [GhostField] public int Vertical;
        [GhostField] public InputEvent Jump;
    }

    internal partial class HostMigrationTests : TestWithSceneAsset
    {
        internal struct UserConnectionComponent : IComponentData
        {
            public int Value1;
            public byte Value2;
        }

        internal struct UserConnectionTagComponent : IComponentData { }

        internal struct SomeBuffer : IBufferElementData, IEnableableComponent
        {
            [GhostField] public int Value;
        }

        internal struct AnotherBuffer : IBufferElementData
        {
            [GhostField] public int ValueOne;
            [GhostField] public int ValueTwo;
        }

        internal struct SimpleData : IComponentData
        {
            [GhostField] public int IntValue;
            [GhostField] public Quaternion QuaternionValue;
            [GhostField] public FixedString128Bytes StringValue;
            public float FloatValue;
        }

        internal struct MoreData : IComponentData
        {
            [GhostField] public int IntValue;
            public float FloatValue;
        }

        [GhostEnabledBit]
        internal struct SomeEnableable : IComponentData, IEnableableComponent
        {
            [GhostField] public int IntValue;
        }

        [GhostComponent(PrefabType = GhostPrefabType.Server)]
        internal struct HostOnlyData : IComponentData
        {
            // TODO：必须至少包含一个 GhostField，否则该组件不会被 Ghost 组件序列化状态追踪
            [GhostField] public int Value;
            public float FloatValue;
            // TODO：当前不支持容器类型，应抛出错误或忽略该字段
            //public NativeArray<int> IntArray;
        }

        [GhostComponent(PrefabType = GhostPrefabType.Server)]
        internal struct HostOnlyBuffer : IBufferElementData
        {
            [GhostField] public int Value;
        }

        [DisableAutoCreation]
        [UpdateInGroup(typeof(GhostInputSystemGroup))]
        internal partial class SetInputSystem : SystemBase
        {
            public static int TargetEventCount;
            public int SendCounter {get; set;}

            public void OnCreate(ref SystemState state)
            {
                SendCounter = 0;
            }

            protected override void OnUpdate()
            {
                foreach (var input in SystemAPI.Query<RefRW<HMRemoteInput>>().WithAll<GhostOwnerIsLocal>())
                {
                    if (SendCounter == TargetEventCount)
                    {
                        input.ValueRW.Horizontal = 0;
                        input.ValueRW.Vertical = 0;
                        input.ValueRW.Jump = default;
                        return;
                    }
                    SendCounter++;
                    input.ValueRW.Vertical = 1;
                    input.ValueRW.Horizontal = 1;
                    input.ValueRW.Jump.Set();
                }

            }
        }

        [DisableAutoCreation]
        [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
        internal partial class GetInputSystem : SystemBase
        {
            public int ReceiveCounter { get; set; }
            public long EventCountValue { get; set; }

            protected override void OnUpdate()
            {
                var networkTime = SystemAPI.GetSingleton<NetworkTime>();
                foreach (var (input, entity) in SystemAPI.Query<RefRW<HMRemoteInput>>().WithAll<Simulate>().WithEntityAccess())
                {
                    if (input.ValueRW.Jump.IsSet && networkTime.IsFirstTimeFullyPredictingTick)
                    {
                        ReceiveCounter++;
                        EventCountValue += input.ValueRW.Jump.Count;
                    }
                }
            }
        }

        // [Test]
        // public unsafe void UseDataWriterWithCompression()
        // {
        //     const int ghostCount = 80;
        //     using (var testWorld = new NetCodeTestWorld())
        //     {
        //         testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
        //         testWorld.CreateWorlds(true, 1);
        //         testWorld.Connect();
        //         testWorld.GoInGame();
        //
        //         CreatePrefab(testWorld.ClientWorlds[0].EntityManager);
        //         var prefab = CreatePrefab(testWorld.ServerWorld.EntityManager);
        //
        //         var ghostList = new NativeList<Entity>(Allocator.Temp);
        //         ref var state = ref testWorld.ServerWorld.Unmanaged.GetExistingSystemState<ServerHostMigrationSystem>();
        //         for (int i = 0; i < ghostCount; ++i)
        //         {
        //             var ghost = testWorld.ServerWorld.EntityManager.Instantiate(prefab);
        //             ghostList.Add(ghost);
        //             testWorld.ServerWorld.EntityManager.SetComponentData(ghost, new GhostOwner() { NetworkId = i+1 });
        //             var beforePosition = new LocalTransform() { Position = new float3(i+1, i+2, i+3) };
        //             testWorld.ServerWorld.EntityManager.SetComponentData(ghost, beforePosition);
        //             var someBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<SomeBuffer>(ghost);
        //             someBuffer.Add(new SomeBuffer() { Value = i+100 });
        //             someBuffer.Add(new SomeBuffer() { Value = i+200 });
        //             someBuffer.Add(new SomeBuffer() { Value = i+300 });
        //             someBuffer.Add(new SomeBuffer() { Value = i+400 });
        //             var anotherBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<AnotherBuffer>(ghost);
        //             anotherBuffer.Add(new AnotherBuffer() { ValueOne = i+1000, ValueTwo = i+2000 });
        //             anotherBuffer.Add(new AnotherBuffer() { ValueOne = i+3000, ValueTwo = i+4000 });
        //         }
        //
        //         // 等待 Ghost 生成
        //         for (int i = 0; i < 2; ++i)
        //             testWorld.Tick();
        //
        //         using var prefabsQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollectionPrefab>());
        //         var prefabs = prefabsQuery.GetSingletonBuffer<GhostCollectionPrefab>();
        //
        //         var ghostStorage = new GhostStorage();
        //         ghostStorage.GhostPrefabs = new NativeArray<GhostPrefabData>(prefabs.Length, Allocator.Temp);
        //         ghostStorage.Ghosts = new NativeArray<GhostData>(ghostList.Length, Allocator.Temp);
        //
        //         for (int i = 0; i < prefabs.Length; ++i)
        //             ghostStorage.GhostPrefabs[i] = new GhostPrefabData(){GhostTypeIndex = i, GhostTypeHash = prefabs[i].GhostType.GetHashCode()};
        //
        //         var hostMigrationCache = new HostMigration.HostMigrationUtility.Data();
        //         hostMigrationCache.ServerOnlyComponentsFlag = new NativeList<int>(64, Allocator.Temp);
        //         hostMigrationCache.ServerOnlyComponentsPerGhostType = new NativeHashMap<int, NativeList<ComponentType>>(64, Allocator.Temp);
        //
        //         var dataList = new NativeList<NativeArray<byte>>(Allocator.Temp);
        //         for (int i = 0; i < ghostList.Length; ++i)
        //         {
        //             var ghostInstance = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ghostList[i]);
        //             var ghostData = HostMigration.HostMigrationUtility.GetGhostComponentData(hostMigrationCache, ref state, ghostList[i], ghostInstance.ghostType, out var hasErrors);
        //             ghostStorage.Ghosts[i] = new GhostData()
        //             {
        //                 GhostId = ghostInstance.ghostId,
        //                 GhostType = ghostInstance.ghostType,
        //                 Data = ghostData
        //             };
        //
        //             dataList.Add(ghostData);
        //             Assert.IsFalse(hasErrors);
        //         }
        //
        //         var ghostDataBlob = new NativeList<byte>(1024, Allocator.Temp);
        //         HostMigrationUtility.SerializeGhostData(ref ghostStorage, ghostDataBlob);
        //
        //         var compressedGhostData = new NativeList<byte>(ghostDataBlob.Length, Allocator.Temp);
        //         HostMigrationUtility.CompressAndEncodeGhostData(ghostDataBlob, compressedGhostData);
        //
        //         var ghostDataSlice = compressedGhostData.AsArray().Slice();
        //         var decodedGhosts = HostMigrationUtility.DecompressAndDecodeGhostData(ghostDataSlice);
        //         Assert.AreEqual(prefabs.Length, decodedGhosts.GhostPrefabs.Length);
        //         for (int i = 0; i < prefabs.Length; ++i)
        //         {
        //             Assert.AreEqual(i, decodedGhosts.GhostPrefabs[i].GhostTypeIndex);
        //             Assert.AreEqual(prefabs[i].GhostType.GetHashCode(), decodedGhosts.GhostPrefabs[i].GhostTypeHash);
        //         }
        //         Assert.AreEqual(ghostList.Length, decodedGhosts.Ghosts.Length);
        //         for (int i = 0; i < ghostList.Length; ++i)
        //         {
        //             var ghostInstance = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ghostList[i]);
        //             Assert.AreEqual(ghostInstance.ghostId, decodedGhosts.Ghosts[i].GhostId);
        //             Assert.AreEqual(ghostInstance.ghostType, decodedGhosts.Ghosts[i].GhostType);
        //             for (int j = 0; j < dataList[i].Length; ++j)
        //                Assert.IsTrue(decodedGhosts.Ghosts[i].Data[j] == dataList[i][j]);
        //         }
        //     }
        // }

        [Test]
        public void HostDataSizeIsCorrect()
        {
            int clientCount = 2;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                // 不使用测试 World 的 Ghost Collection 烘焙流程，因为它依赖自定义生成方式
                // Host Migration 必须验证普通 Ghost 生成流程
                for (int i = 0; i < clientCount; ++i)
                    CreatePrefabWithOnlyComponents(testWorld.ClientWorlds[i].EntityManager);
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefabWithOnlyComponents(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps:10);
                testWorld.GoInGame();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                Assert.AreEqual(1, serverPrefabs.Length);

                CreateServerGhosts(5, testWorld, serverPrefabs[0].GhostPrefab);

                for (int i = 0; i < 5; ++i)
                    testWorld.Tick();

                // 等待 Host Migration 数据采集完成
                var migrationData = new NativeList<byte>(Allocator.Temp);
                var currentTime = testWorld.ServerWorld.Time.ElapsedTime;
                var migrationStats = testWorld.GetSingleton<HostMigrationStats>(testWorld.ServerWorld);
                var timeout = currentTime + 10;
                while (migrationStats.LastDataUpdateTime < currentTime)
                {
                    testWorld.Tick();
                    migrationStats = testWorld.GetSingleton<HostMigrationStats>(testWorld.ServerWorld);
                    if (testWorld.ServerWorld.Time.ElapsedTime > timeout)
                        Assert.Fail("Timeout while waiting for host migration data update");
                }

                var hostMigrationData = testWorld.GetSingletonRW<HostMigrationStorage>(testWorld.ServerWorld);
                var hostData = hostMigrationData.ValueRO.HostDataBlob;
                var ghostData = hostMigrationData.ValueRO.GhostDataBlob;

                var compressedGhostData = new NativeList<byte>(migrationData.Length, Allocator.Temp);
                HostMigrationData.CompressAndEncodeGhostData(ghostData, compressedGhostData);
                Assert.IsTrue(ghostData.Length > compressedGhostData.Length);

                var expectedSize = hostData.Length + compressedGhostData.Length + 2*sizeof(int);

                HostMigrationData.Get(testWorld.ServerWorld, ref migrationData);
                Assert.AreEqual(expectedSize, migrationData.Length);
                Assert.AreEqual(0, migrationData[^1]);  // 最后一个字节始终为 0

                migrationStats = testWorld.GetSingleton<HostMigrationStats>(testWorld.ServerWorld);
                Assert.AreEqual(expectedSize, migrationStats.UpdateSize);
                Assert.AreEqual(expectedSize, migrationStats.TotalUpdateSize);
            }
        }

        [Test]
        public void HostOnlyStateIsMigrated()
        {
            int clientCount = 2;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                // 不使用测试 World 的 Ghost Collection 烘焙流程，因为它依赖自定义生成方式
                // Host Migration 必须验证普通 Ghost 生成流程
                for (int i = 0; i < clientCount; ++i)
                    CreateHostDataPrefab(testWorld.ClientWorlds[i].EntityManager);
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreateHostDataPrefab(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps:10);
                testWorld.GoInGame();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                Assert.AreEqual(1, serverPrefabs.Length);

                var hostDataEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[0].GhostPrefab);
                var hostArray = new NativeArray<int>(3, Allocator.Persistent);
                hostArray[0] = 1;
                hostArray[1] = 2;
                hostArray[2] = 3;
                testWorld.ServerWorld.EntityManager.SetComponentData(hostDataEntity, new HostOnlyData(){ Value = hostArray.Length, FloatValue = 100f});
                var hostDataBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<HostOnlyBuffer>(hostDataEntity);
                hostDataBuffer.Add(new HostOnlyBuffer() { Value = 100 });
                hostDataBuffer.Add(new HostOnlyBuffer() { Value = 200 });
                hostDataBuffer.Add(new HostOnlyBuffer() { Value = 300 });
                hostDataBuffer.Add(new HostOnlyBuffer() { Value = 400 });

                for (int i = 0; i < 5; ++i)
                    testWorld.Tick();

                // 每个 World 中都应生成一个 Ghost
                var serverGhostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                Assert.AreEqual(1, serverGhostQuery.CalculateEntityCount());
                var serverComponentCount = testWorld.ServerWorld.EntityManager.GetComponentTypes(serverGhostQuery.GetSingletonEntity()).Length;
                for (int i = 0; i < clientCount; ++i)
                {
                    // 客户端应存在对应 Ghost，但不包含两个仅服务端组件
                    using var clientGhostQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                    Assert.AreEqual(1, clientGhostQuery.CalculateEntityCount());
                    Assert.AreEqual(serverComponentCount - 2, testWorld.ClientWorlds[i].EntityManager.GetComponentTypes(clientGhostQuery.GetSingletonEntity()).Length);
                }

                GetHostMigrationData(testWorld, out var migrationData);

                // 销毁当前服务端并创建新的服务端 World
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);
                WaitForClientDisconnect(testWorld, clientCount);

                // 恢复 Prefab 和 Ghost Collection，真实迁移流程通常由 SubScene 加载完成
                CreateHostDataPrefab(testWorld.ServerWorld.EntityManager);

                // 客户端索引 0 将成为新 Host 的本地客户端，因此不重新连接，后续从索引 1 开始处理
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 1; i < clientCount; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // TODO：客户端连接恢复尚未自动处理，因此需要手动加入游戏状态
                for (int i = 1; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // 推进 Tick，让 Ghost Collection System 完成处理
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                // 验证客户端与服务端的 Ghost Collection 均正确
                var serverCollection = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var prefabBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionPrefab>(serverCollection);
                Assert.AreEqual(1, prefabBuffer.Length);
                for (int i = 1; i < clientCount; ++i)
                {
                    var clientCollection = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ClientWorlds[i]);
                    prefabBuffer = testWorld.ClientWorlds[i].EntityManager.GetBuffer<GhostCollectionPrefab>(clientCollection);
                    Assert.AreEqual(1, prefabBuffer.Length);
                }

                // 验证迁移后的 Ghost 生成结果与仅服务端数据
                using var ghostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                var ghostEntities = ghostQuery.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(1, ghostEntities.Length);
                var hostOnlyData = testWorld.ServerWorld.EntityManager.GetComponentData<HostOnlyData>(ghostEntities[0]);
                Assert.AreEqual(hostArray.Length, hostOnlyData.Value);
                Assert.AreEqual(100f, hostOnlyData.FloatValue);
                var hostBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<HostOnlyBuffer>(ghostEntities[0]);
                Assert.AreEqual(4, hostBuffer.Length);
                Assert.AreEqual(100, hostBuffer[0].Value);
                Assert.AreEqual(200, hostBuffer[1].Value);
                Assert.AreEqual(300, hostBuffer[2].Value);
                Assert.AreEqual(400, hostBuffer[3].Value);
            }
        }

        /// <summary>
        /// 最基础的 Host Migration 场景：一个本地 Host、多个已连接客户端，以及每个客户端对应的玩家 Ghost
        /// </summary>
        [Test]
        [TestCase(5, 500)]
        [TestCase(3, 1)]
        [TestCase(2, 0)]
        public void SimpleHostMigrationScenario(int clientCount, int serverGhostCount)
        {
            // 注意：迁移前创建的 Query 不能通过 using 自动释放，迁移完成后创建的 Query 不受此限制
            // 测试会手动销毁旧服务端 World 并创建新 World
            // 若离开作用域时再次释放属于旧 World 的 Query，会触发异常
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                // 不使用测试 World 的 Ghost Collection 烘焙流程，因为它依赖自定义生成方式
                // Host Migration 必须验证普通 Ghost 生成流程
                for (int i = 0; i < clientCount; ++i)
                {
                    CreatePrefab(testWorld.ClientWorlds[i].EntityManager);
                    CreatePrefabWithOnlyComponents(testWorld.ClientWorlds[i].EntityManager);
                }
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefab(testWorld.ServerWorld.EntityManager);
                CreatePrefabWithOnlyComponents(testWorld.ServerWorld.EntityManager);
                int prefabCount = 2; // 数量稍后会验证，并用于迁移请求

                testWorld.Connect(maxSteps:10);
                testWorld.GoInGame();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // TODO：在不同 Tick 生成 Ghost 并验证 SpawnTick
                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                Assert.AreEqual(prefabCount, serverPrefabs.Length);

                CreatePlayerGhosts(clientCount, testWorld, serverPrefabs[0].GhostPrefab);
                CreateServerGhosts(serverGhostCount, testWorld, serverPrefabs[1].GhostPrefab);

                for (int i = 0; i < 200; ++i)
                    testWorld.Tick();

                // 应包含每个客户端的玩家 Ghost 以及全部服务端所有 Ghost
                var allGhostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                Assert.AreEqual(clientCount + serverGhostCount, allGhostQuery.CalculateEntityCount());
                for (int i = 0; i < clientCount; ++i)
                {
                    using var clientGhostQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                    Assert.AreEqual(clientCount + serverGhostCount, clientGhostQuery.CalculateEntityCount());
                }

                // 保存已生成 Ghost 的 GhostType，供迁移后比较
                var ghostTypeQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostType>());
                var beforeGhostType = ghostTypeQuery.ToComponentDataArray<GhostType>(Allocator.Temp)[0];

                // 向服务端连接实体添加用户组件，这些组件应随连接迁移
                var serverConnectionQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                var serverConnectionEntities = serverConnectionQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < serverConnectionEntities.Length; ++i)
                {
                    testWorld.ServerWorld.EntityManager.AddComponent<UserConnectionTagComponent>(serverConnectionEntities[i]);
                    testWorld.ServerWorld.EntityManager.AddComponentData(serverConnectionEntities[i], new UserConnectionComponent(){ Value1 = i+1, Value2 = 255});
                }

                GetHostMigrationData(testWorld, out var migrationData);

                // 销毁当前服务端并创建新的服务端 World
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                using var hostMigrationDataQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HostMigrationStorage>());
                var hostMigrationData = hostMigrationDataQuery.ToComponentDataArray<HostMigrationStorage>(Allocator.Temp);
                Assert.AreEqual(1, hostMigrationData.Length);

                // 验证保存的连接组件数量与实际一致
                for (int i = 0; i < clientCount; ++i)
                    Assert.AreEqual(2, hostMigrationData[0].HostData.Connections[i].Components.Length);

                // 恢复 Prefab 和 Ghost Collection，真实迁移流程通常由 SubScene 加载完成
                CreatePrefab(testWorld.ServerWorld.EntityManager);
                CreatePrefabWithOnlyComponents(testWorld.ServerWorld.EntityManager);

                WaitForClientDisconnect(testWorld, clientCount);

                // 客户端索引 0 将成为新 Host 的本地客户端，因此不重新连接，后续从索引 1 开始处理
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 1; i < clientCount; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // TODO：客户端连接恢复尚未自动处理，因此需要手动加入游戏状态
                for (int i = 1; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // 推进 Tick，让 Ghost Collection System 完成处理
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                // 验证新服务端连接包含迁移前添加的用户组件
                using var userComponentQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<UserConnectionComponent>(), ComponentType.ReadOnly<UserConnectionTagComponent>(), ComponentType.ReadOnly<NetworkStreamConnection>());
                Assert.AreEqual(clientCount-1, userComponentQuery.CalculateEntityCount());
                var userComponents = userComponentQuery.ToComponentDataArray<UserConnectionComponent>(Allocator.Temp);
                for (int i = 0; i < userComponents.Length; ++i)
                {
                    Assert.AreEqual(i+2, userComponents[i].Value1);
                    Assert.AreEqual(255, userComponents[i].Value2);
                }

                // 验证客户端与服务端的 Ghost Collection 均正确
                var serverCollection = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var prefabBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionPrefab>(serverCollection);
                Assert.AreEqual(prefabCount, prefabBuffer.Length);
                for (int i = 1; i < clientCount; ++i)
                {
                    var clientCollection = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ClientWorlds[i]);
                    prefabBuffer = testWorld.ClientWorlds[i].EntityManager.GetBuffer<GhostCollectionPrefab>(clientCollection);
                    Assert.AreEqual(prefabCount, prefabBuffer.Length);
                }

                // 验证各处的 GhostType 均保持正确
                using var ghostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostOwner>(), ComponentType.ReadOnly<GhostType>(), ComponentType.ReadOnly<LocalTransform>());
                var ghostTypes = ghostQuery.ToComponentDataArray<GhostType>(Allocator.Temp);
                for (int i = 0; i < clientCount-1; ++i)
                    Assert.AreEqual(beforeGhostType, ghostTypes[i]);

                ValidatePlayerGhosts(clientCount-1, testWorld);
                using var serverGhostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SimpleData>(), ComponentType.ReadOnly<MoreData>());
                Assert.AreEqual(serverGhostCount, serverGhostQuery.CalculateEntityCount());
                var someData = serverGhostQuery.ToComponentDataArray<SimpleData>(Allocator.Temp);
                var moreData = serverGhostQuery.ToComponentDataArray<MoreData>(Allocator.Temp);
                for (int i = 0; i < serverGhostCount - 1; ++i)
                {
                    Assert.AreEqual(new SimpleData(){ FloatValue = 100f + i, IntValue = 100 + i, QuaternionValue = Quaternion.Euler(1,2,3), StringValue = "HelloWorldHelloWorldHelloWorld"}, someData[i]);
                    Assert.AreEqual(new MoreData(){ IntValue = 1000 + i, FloatValue = 1000f + i }, moreData[i]);
                }
            }
        }

        static void DisconnectServerAndCreateNewServerWorld(NetCodeTestWorld testWorld, ref NativeList<byte> migrationData)
        {
            var serverConnectionQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
            var connections = serverConnectionQuery.ToComponentDataArray<NetworkStreamConnection>(Allocator.Temp);
            for (int i = 0; i < connections.Length; ++i)
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.DriverStore.Disconnect(connections[i]);
            testWorld.Tick();
            testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.DriverStore.Dispose();
            var serverNetDebugQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetDebug>());
            var serverNetDebug = serverNetDebugQuery.GetSingleton<NetDebug>();
            var driverStore = new NetworkDriverStore();
            NetworkStreamReceiveSystem.DriverConstructor.CreateServerDriver(testWorld.ServerWorld, ref driverStore, serverNetDebug);
            var serverDriver = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
            serverDriver.ResetDriverStore(testWorld.ServerWorld.Unmanaged, ref driverStore);
            testWorld.ServerWorld.Dispose();
            testWorld.ServerWorld = testWorld.CreateServerWorld("HostMigrationServerWorld");
            testWorld.TrySuppressNetDebug(true, true);
            testWorld.Tick();

            HostMigrationData.Set(migrationData.AsArray(), testWorld.ServerWorld);
        }

        /// <summary>
        /// 验证各组件的可启用状态在迁移过程中得到正确传递
        /// </summary>
        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public void EnablableComponentsMigrateProperly(bool setAsEnabled)
        {
            int clientCount = 3;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                // 不使用测试 World 的 Ghost Collection 烘焙流程，因为它依赖自定义生成方式
                // Host Migration 必须验证普通 Ghost 生成流程
                for (int i = 0; i < clientCount; ++i)
                    CreatePrefabWithEnableable(testWorld.ClientWorlds[i].EntityManager);
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefabWithEnableable(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps:10);
                testWorld.GoInGame();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);

                // 在服务端为每个客户端创建 Ghost，并将所有者设为对应连接
                var playerEntities = new NativeList<Entity>(Allocator.Temp);
                for (int i = 0; i < clientCount; ++i)
                {
                    var playerEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[0].GhostPrefab);
                    playerEntities.Add(playerEntity);
                    var beforePosition = new LocalTransform() { Position = new float3(i+1, i+2, i+3) };
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, beforePosition);
                    var someBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<SomeBuffer>(playerEntity);
                    someBuffer.Add(new SomeBuffer() { Value = i+100 });
                    someBuffer.Add(new SomeBuffer() { Value = i+200 });
                    someBuffer.Add(new SomeBuffer() { Value = i+300 });
                    someBuffer.Add(new SomeBuffer() { Value = i+400 });
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new SomeEnableable() { IntValue = i+1 });
                }

                // 设置所有 Ghost 上组件与 Buffer 的启用位
                for (int i = 0; i < playerEntities.Length; ++i)
                {
                    testWorld.ServerWorld.EntityManager.SetComponentEnabled<SomeEnableable>(playerEntities[i], setAsEnabled);
                    testWorld.ServerWorld.EntityManager.SetComponentEnabled<SomeBuffer>(playerEntities[i], setAsEnabled);
                }

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // 验证客户端上的启用位状态
                for (int i = 0; i < clientCount; ++i)
                {
                    using var clientQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SomeEnableable>());
                    var clientGhostEntities = clientQuery.ToEntityArray(Allocator.Temp);
                    for (int j = 0; j < clientGhostEntities.Length; ++j)
                    {
                        Assert.AreEqual(setAsEnabled, testWorld.ClientWorlds[i].EntityManager.IsComponentEnabled<SomeEnableable>(clientGhostEntities[i]));
                        Assert.AreEqual(setAsEnabled, testWorld.ClientWorlds[i].EntityManager.IsComponentEnabled<SomeBuffer>(clientGhostEntities[i]));
                    }
                }

                GetHostMigrationData(testWorld, out var migrationData);
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                WaitForClientDisconnect(testWorld, clientCount);

                // 恢复 Prefab 和 Ghost Collection，真实迁移流程通常由 SubScene 加载完成
                CreatePrefabWithEnableable(testWorld.ServerWorld.EntityManager);

                // 客户端索引 0 将成为新 Host 的本地客户端，因此不重新连接，后续从索引 1 开始处理
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 1; i < clientCount; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // TODO：客户端连接恢复尚未自动处理，因此需要手动加入游戏状态
                for (int i = 1; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // 推进 Tick，让 Ghost Collection System 完成处理
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                // 验证迁移后的启用位与 Buffer 数据
                using var ghostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SomeEnableable>());
                var ghostEntities = ghostQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < ghostEntities.Length; ++i)
                {
                    Assert.AreEqual(setAsEnabled, testWorld.ServerWorld.EntityManager.IsComponentEnabled<SomeEnableable>(ghostEntities[i]));
                    Assert.AreEqual(setAsEnabled, testWorld.ServerWorld.EntityManager.IsComponentEnabled<SomeBuffer>(ghostEntities[i]));
                    var someBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<SomeBuffer>(ghostEntities[i]);
                    Assert.AreEqual(4, someBuffer.Length);
                    Assert.AreEqual(100+i, someBuffer[0].Value);
                    Assert.AreEqual(200+i, someBuffer[1].Value);
                    Assert.AreEqual(300+i, someBuffer[2].Value);
                    Assert.AreEqual(400+i, someBuffer[3].Value);
                }
            }
        }

        [Test]
        public void GhostDataBlobSizeGrowsWithGhostCount()
        {
            int clientCount = 3;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                for (int i = 0; i < clientCount; ++i)
                {
                    CreatePrefab(testWorld.ClientWorlds[i].EntityManager);
                    CreatePrefabWithOnlyComponents(testWorld.ClientWorlds[i].EntityManager);
                }
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                var config = testWorld.GetSingletonRW<HostMigrationConfig>(testWorld.ServerWorld);
                config.ValueRW.StoreOwnGhosts = true;
                CreatePrefab(testWorld.ServerWorld.EntityManager);
                CreatePrefabWithOnlyComponents(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps:10);
                testWorld.GoInGame();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);

                CreatePlayerGhosts(clientCount, testWorld, serverPrefabs[0].GhostPrefab);
                var serverGhostCount = 10;
                CreateServerGhosts(serverGhostCount, testWorld, serverPrefabs[1].GhostPrefab);

                // 逐步增加 Ghost 数量，迫使 Ghost 数据 Blob 扩容
                for (int i = 0; i < 10; ++i)
                {
                    GetHostMigrationData(testWorld, out _);
                    testWorld.ServerWorld.EntityManager.CompleteAllTrackedJobs();
                    serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);

                    CreateServerGhosts(10, testWorld, serverPrefabs[1].GhostPrefab, serverGhostCount);
                    serverGhostCount += 10;

                    // 等待下一次采集，否则会直接复用上一份 Host Migration 数据
                    for (int j = 0; j < 4; ++j)
                        testWorld.Tick();
                }

                // 最后执行一次完整 Host Migration
                GetHostMigrationData(testWorld, out var migrationData);
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);
                WaitForClientDisconnect(testWorld, clientCount);
                CreatePrefab(testWorld.ServerWorld.EntityManager);
                CreatePrefabWithOnlyComponents(testWorld.ServerWorld.EntityManager);
                testWorld.Connect(maxSteps:10);
                testWorld.GoInGame();

                // 推进 Tick，让 Ghost Collection System 完成处理
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                ValidatePlayerGhosts(clientCount, testWorld, skipHostOwnedPlayer: false);
                ValidateServerGhosts(serverGhostCount, testWorld);
            }
        }

        [Test]
        public void InputBufferIsSkipped()
        {
            int clientCount = 2;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                for (int i = 0; i < clientCount; ++i)
                    CreatePrefabWithInputs(testWorld.ClientWorlds[i].EntityManager);
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefabWithInputs(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps: 10);
                testWorld.GoInGame();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // 在服务端生成玩家，并将所有者设为对应客户端连接
                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                Assert.AreEqual(1, serverPrefabs.Length);
                for (int i = 0; i < clientCount; ++i)
                {
                    // 写入测试数据，确保 Host 保存和恢复不会覆盖它
                    var playerEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[0].GhostPrefab);
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new GhostOwner() { NetworkId = i+1 });
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new LocalTransform() { Position = new float3(i+1, i+2, i+3) });
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new SimpleData() {FloatValue = i+1, IntValue = i+1, QuaternionValue = new Quaternion(i+1, i+2, i+3, i+4), StringValue = $"HelloWorldHelloWorldHelloWorld"});
                }

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                for (int i = 0; i < 20; ++i)
                {
                    var ghostsQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HMRemoteInput>(), ComponentType.ReadOnly<InputBufferData<HMRemoteInput>>(), ComponentType.ReadOnly<GhostOwner>());
                    var ghostEntities = ghostsQuery.ToEntityArray(Allocator.Temp);
                    for (int k = 0; k < ghostEntities.Length; ++k)
                    {
                        var inputs = testWorld.ServerWorld.EntityManager.GetBuffer<InputBufferData<HMRemoteInput>>(ghostEntities[k]);

                        if (inputs.Length == 0)
                        {
                            inputs.Add(new InputBufferData<HMRemoteInput>() { InternalInput = new HMRemoteInput() { Horizontal = 1, Vertical = 1 } });
                        }
                        else
                        {
                            var prevInput = inputs[^1];
                            inputs.Add(new InputBufferData<HMRemoteInput>() { InternalInput = new HMRemoteInput() { Horizontal = ++prevInput.InternalInput.Horizontal, Vertical = ++prevInput.InternalInput.Vertical } });
                        }
                    }

                    testWorld.Tick();
                }

                GetHostMigrationData(testWorld, out var migrationData);
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                // 验证保存数据的预期大小，并确认输入 Buffer 已被跳过
                //   Unity.NetCode.GhostInstance - 12 字节
                //   Unity.NetCode.GhostOwner - 4 字节
                //   Unity.Transforms.LocalTransform - 32 字节
                //   Unity.NetCode.Tests.HostMigrationTests+SomeData - 152 字节
                //   Unity.NetCode.AutoCommandTarget - 1 字节
                //   Unity.NetCode.InputBufferData`1<Unity.NetCode.Tests.HMRemoteInput> - 320 字节，应跳过
                using var hostMigrationDataQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HostMigrationStorage>());
                var hostMigrationData = hostMigrationDataQuery.ToComponentDataArray<HostMigrationStorage>(Allocator.Temp);
                Assert.AreEqual(1, hostMigrationData.Length);
                var dataSize = 0;
                foreach (var ghost in hostMigrationData[0].Ghosts.Ghosts)
                {
                    foreach (var component in ghost.DataComponents)
                    {
                        var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(component.StableHash);
                        var componentType = ComponentType.FromTypeIndex(typeIndex);
                        Assert.AreNotEqual(componentType, ComponentType.ReadWrite<InputBufferData<HMRemoteInput>>());
                        dataSize += component.Data.Length;
                    }
                }
                Assert.AreEqual(201, dataSize);

                CreatePrefabWithInputs(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps:10);

                // TODO：客户端连接恢复尚未自动处理，因此需要手动加入游戏状态
                for (int i = 1; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // 推进 Tick，让 Ghost Collection System 完成处理
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                // 验证迁移后的 Ghost 数据保持完整
                using var ghostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostOwner>(), ComponentType.ReadOnly<GhostType>(), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<SimpleData>());
                var ghostOwners = ghostQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                var ghostPositions = ghostQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                var someDatas = ghostQuery.ToComponentDataArray<SimpleData>(Allocator.Temp);
                for (int i = 0; i < clientCount-1; ++i)
                {
                    Assert.AreEqual(i+2, ghostOwners[i].NetworkId);     // 首个客户端也会重连并获得 ID 1，但其玩家 Ghost 在保存 Host 数据时已移除
                    Assert.AreEqual(new float3(i+2, i+3, i+4), ghostPositions[i].Position); // 原首个连接的位置为 (1,2,3)，因此剩余数据从 (2,3,4) 开始
                    Assert.AreEqual(new SimpleData(){FloatValue = i+2, IntValue = i+2, QuaternionValue = new Quaternion(i+2,i+3,i+4,i+5), StringValue = "HelloWorldHelloWorldHelloWorld"}, someDatas[i]);
                }
            }
        }

        [Test]
        public void MigrationWithMultiplePrefabTypes()
        {
            int clientCount = 3;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                // 创建两种不同 Prefab，使其进入不同 Chunk，用于验证复制数据时能正确遍历多个 Chunk
                for (int i = 0; i < clientCount; i++)
                {
                    CreatePrefab(testWorld.ClientWorlds[i].EntityManager);
                    CreatePrefabTypeTwo(testWorld.ClientWorlds[i].EntityManager);
                }
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefab(testWorld.ServerWorld.EntityManager);
                CreatePrefabTypeTwo(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps:10);
                testWorld.GoInGame();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                Assert.AreEqual(2, serverPrefabs.Length);
                CreatePlayerGhosts(clientCount, testWorld, serverPrefabs[0].GhostPrefab);

                // 生成若干第二种 Prefab 实体
                const int miscEntityCount = 5;
                for (int i = 0; i < miscEntityCount; ++i)
                {
                    var miscEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[1].GhostPrefab);
                    testWorld.ServerWorld.EntityManager.SetComponentData(miscEntity, new SimpleData() {FloatValue = i+1, IntValue = i+1, QuaternionValue = new Quaternion(i+1,i+2,i+3,i+4), StringValue = "HelloWorldHelloWorldHelloWorld"});
                    testWorld.ServerWorld.EntityManager.SetComponentData(miscEntity, new LocalTransform(){Position = new float3(i+1, i+2, i+3)});
                }

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // 每个客户端应对应一个玩家 Ghost，此外还应包含全部第二种 Prefab 实体
                var serverGhostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                Assert.AreEqual(clientCount+miscEntityCount, serverGhostQuery.CalculateEntityCount());
                for (int i = 0; i < clientCount; ++i)
                {
                    using var clientGhostQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                    Assert.AreEqual(clientCount+miscEntityCount, clientGhostQuery.CalculateEntityCount());
                }

                GetHostMigrationData(testWorld, out var migrationData);
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                // 恢复 Prefab 和 Ghost Collection，真实迁移流程通常由 SubScene 加载完成
                // 以相反顺序创建 Prefab，确保新旧服务端的 GhostType 索引不一致
                CreatePrefabTypeTwo(testWorld.ServerWorld.EntityManager);
                CreatePrefab(testWorld.ServerWorld.EntityManager);

                // 客户端索引 0 将成为新 Host 的本地客户端，因此不重新连接，后续从索引 1 开始处理
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 1; i < clientCount; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // TODO：客户端连接恢复尚未自动处理，因此需要手动加入游戏状态
                for (int i = 1; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // 推进 Tick，让 Ghost Collection System 完成处理
                for (int i = 0; i < 6; ++i)
                    testWorld.Tick();

                // 验证第二种 Prefab 的数据未被破坏，这 5 个 Ghost 不属于任何客户端玩家，因此都应保留
                using var ghostServerQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<SimpleData>(), ComponentType.ReadOnly<LocalTransform>());
                var ghostServerPositions = ghostServerQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                var serverSomeDatas = ghostServerQuery.ToComponentDataArray<SimpleData>(Allocator.Temp);
                for (int i = 0; i < ghostServerPositions.Length; ++i)
                {
                    Assert.AreEqual(new float3(i+1, i+2, i+3), ghostServerPositions[i].Position);
                    Assert.AreEqual(new SimpleData(){FloatValue = i+1, IntValue = i+1, QuaternionValue = new Quaternion(i+1,i+2,i+3,i+4), StringValue = "HelloWorldHelloWorldHelloWorld"}, serverSomeDatas[i]);
                }
            }
        }

        [Test]
        public void MigrationWithPrespawnGhosts()
        {
            var clientCount = 10;
            VerifyGhostIds.GhostsPerScene = 25;

            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent), typeof(SomeDataAuthoring));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub0", 5, 5, ghost, Vector3.zero);
            SceneManager.SetActiveScene(scene);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateWorlds(true, clientCount);

                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableHostMigration));
                foreach (var client in testWorld.ClientWorlds)
                {
                    client.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                    client.EntityManager.CreateEntity(typeof(EnableHostMigration));
                }

                testWorld.Connect();
                testWorld.GoInGame();

                // 确认 Prespawn Ghost 已全部加载，GhostsPerScene 应与场景实际数量一致
                for(int i=0;i<64;++i)
                {
                    testWorld.Tick();
                    var clientMatched = true;
                    foreach (var client in testWorld.ClientWorlds)
                        clientMatched &= client.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene;
                    if (testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene &&
                        clientMatched)
                        break;
                }

                // 修改全部 Prespawn Ghost 的位置和测试数据
                var prespawnGhostPositionsQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(LocalTransform), typeof(GhostInstance));
                var prespawnGhostPositions = prespawnGhostPositionsQuery.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawnGhostPositions.Length);
                for (int i = 0; i < prespawnGhostPositions.Length; ++i)
                {
                    testWorld.ServerWorld.EntityManager.SetComponentData(prespawnGhostPositions[i], new LocalTransform(){Position = new float3(i+1, i+2, i+3)});
                    testWorld.ServerWorld.EntityManager.SetComponentData(prespawnGhostPositions[i], new SomeData(){Value = i});
                }
                for(int i=0;i<64;++i)
                    testWorld.Tick();
                // 验证位置变化已同步到客户端
                foreach (var client in testWorld.ClientWorlds)
                {
                    using var clientPrespawnQuery = client.EntityManager.CreateEntityQuery(typeof(LocalTransform), typeof(GhostInstance));
                    var clientPositions = clientPrespawnQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                    Assert.AreEqual(VerifyGhostIds.GhostsPerScene, clientPositions.Length);
                    for (int i = 0; i < clientPositions.Length; ++i)
                    {
                        var expectedPosition = new float3(i+1, i+2, i+3);
                        Assert.AreEqual(expectedPosition.x, clientPositions[i].Position.x, 0.001);
                        Assert.AreEqual(expectedPosition.y, clientPositions[i].Position.y, 0.001);
                        Assert.AreEqual(expectedPosition.z, clientPositions[i].Position.z, 0.001);
                    }
                }

                testWorld.ServerWorld.EntityManager.CompleteAllTrackedJobs();
                GetHostMigrationData(testWorld, out var migrationData);
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                // 客户端索引 0 将成为新 Host 的本地客户端，因此不重新连接，后续从索引 1 开始处理
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 1; i < clientCount; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // 在客户端和服务端两侧将重连连接加入游戏状态
                using var newServerConnectionsQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkId));
                var newServerConnections = newServerConnectionsQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < newServerConnections.Length; ++i)
                    testWorld.ServerWorld.EntityManager.AddComponent<NetCode.NetworkStreamInGame>(newServerConnections[i]);
                for (int i = 1; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // 推进 Tick，让 Ghost Collection System 完成处理
                for (int i = 0; i < 6; ++i)
                    testWorld.Tick();

                // 验证新服务端将 Prespawn Ghost 恢复到正确位置
                using var ghostServerQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(LocalTransform), typeof(GhostInstance));
                var ghostServerPositions = ghostServerQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, ghostServerPositions.Length);
                for (int i = 0; i < ghostServerPositions.Length; ++i)
                {
                    Assert.AreEqual(new float3(i+1, i+2, i+3), ghostServerPositions[i].Position);
                }

                // 验证客户端也保持正确位置和测试数据
                foreach (var client in testWorld.ClientWorlds)
                {
                    using var clientPrespawnQuery = client.EntityManager.CreateEntityQuery(typeof(LocalTransform), typeof(GhostInstance), typeof(SomeData));
                    var clientPositions = clientPrespawnQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                    var clientSomeData = clientPrespawnQuery.ToComponentDataArray<SomeData>(Allocator.Temp);
                    Assert.AreEqual(VerifyGhostIds.GhostsPerScene, clientPositions.Length);
                    for (int i = 0; i < clientPositions.Length; ++i)
                    {
                        var expectedPosition = new float3(i+1, i+2, i+3);
                        Assert.AreEqual(expectedPosition.x, clientPositions[i].Position.x, 0.001);
                        Assert.AreEqual(expectedPosition.y, clientPositions[i].Position.y, 0.001);
                        Assert.AreEqual(expectedPosition.z, clientPositions[i].Position.z, 0.001);
                        Assert.AreEqual(i, clientSomeData[i].Value);
                    }
                }
            }
        }

        /// <summary>
        /// 在 Host Migration 前后通过输入组件发送 InputEvent，并检查迁移前后的事件计数器
        /// 如果迁移输入 Buffer，客户端重连后发送新事件时可能出现计数问题
        /// 递减事件逻辑会尝试从新客户端的初始值 0 中减去迁移前观察到的计数
        /// 不迁移输入 Buffer 时，新 Host 与客户端两端的计数都会从 0 开始
        /// </summary>
        [Test]
        public void InputEventCountsWorkAfterMigration()
        {
            int clientCount = 4;
            using (var testWorld = new NetCodeTestWorld())
            {
                SetInputSystem.TargetEventCount = 5;
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem), typeof(SetInputSystem), typeof(GetInputSystem));
                testWorld.CreateWorlds(true, clientCount);

                for (int i = 0; i < clientCount; ++i)
                {
                    testWorld.ClientWorlds[i].EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                    CreatePrefabWithInputs(testWorld.ClientWorlds[i].EntityManager);
                }
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefabWithInputs(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps: 10);
                testWorld.GoInGame();

                // 推进 Tick，让 Ghost Collection 完成初始化
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // 在服务端生成玩家，并将所有者设为对应客户端连接
                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                var connectionsOnServerQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                var networkIdsOnServer = connectionsOnServerQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
                var connectionsOnServer = connectionsOnServerQuery.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(1, serverPrefabs.Length);
                for (int i = 0; i < clientCount; ++i)
                {
                    // 写入测试数据，确保 Host 保存和恢复不会覆盖它
                    var networkId = networkIdsOnServer[i].Value;
                    var playerEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[0].GhostPrefab);
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new GhostOwner() { NetworkId = networkId });
                    testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new LocalTransform() { Position = new float3(i+1, i+2, i+3) });
                    testWorld.ServerWorld.EntityManager.SetComponentData(connectionsOnServer[i], new CommandTarget(){ targetEntity = playerEntity});
                }

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // 同时在客户端设置 CommandTarget 与本地所有者标记
                foreach (var world in testWorld.ClientWorlds)
                {
                    var connectionOnClient = testWorld.TryGetSingletonEntity<NetworkId>(world);
                    var playerOnClient = testWorld.TryGetSingletonEntity<HMRemoteInput>(world);
                    world.EntityManager.SetComponentData(connectionOnClient, new CommandTarget{targetEntity = playerOnClient});
                    world.EntityManager.AddComponent<GhostOwnerIsLocal>(playerOnClient);
                }

                // 推进 Tick，让输入 System 发送完全部目标事件
                for (int i = 0; i < SetInputSystem.TargetEventCount * 2; ++i)
                    testWorld.Tick();

                for (int i = 0; i < clientCount; ++i)
                {
                    var setInputSystem = testWorld.ClientWorlds[i].GetExistingSystemManaged<SetInputSystem>();
                    var getInputSystem = testWorld.ClientWorlds[i].GetExistingSystemManaged<GetInputSystem>();
                    Assert.AreEqual(SetInputSystem.TargetEventCount, setInputSystem.SendCounter);
                    Assert.AreEqual(SetInputSystem.TargetEventCount, getInputSystem.ReceiveCounter);
                    // TODO：EventCountValue 应与 ReceiveCounter 相等
                    Assert.Greater(10000, getInputSystem.EventCountValue);
                }
                var serverInputSystem = testWorld.ServerWorld.GetExistingSystemManaged<GetInputSystem>();
                Assert.AreEqual(clientCount * SetInputSystem.TargetEventCount, serverInputSystem.ReceiveCounter);
                Assert.Greater(10000, serverInputSystem.EventCountValue);

                // 此处在同一 Tick 保存迁移数据并恢复到新服务端，不模拟定期上传外部服务产生的延迟
                testWorld.ServerWorld.EntityManager.CompleteAllTrackedJobs();
                GetHostMigrationData(testWorld, out var migrationData);
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                // 重置客户端输入 System 计数器
                for (int i = 0; i < clientCount; ++i)
                {
                    var setInputSystem = testWorld.ClientWorlds[i].GetExistingSystemManaged<SetInputSystem>();
                    setInputSystem.SendCounter = 0;
                    var getInputSystem = testWorld.ClientWorlds[i].GetExistingSystemManaged<GetInputSystem>();
                    getInputSystem.ReceiveCounter = 0;
                    getInputSystem.EventCountValue = 0;
                }

                // 推进 Host Migration System，必须先恢复全部 Ghost 才能处理客户端重连
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                CreatePrefabWithInputs(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps:10);

                // 在新服务端设置 CommandTarget
                using var playerEntitiesQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());
                var playerEntities = playerEntitiesQuery.ToEntityArray(Allocator.Temp);
                var playerGhostOwner = playerEntitiesQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                using var connectionsOnNewServerQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                networkIdsOnServer = connectionsOnNewServerQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
                connectionsOnServer = connectionsOnNewServerQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < playerGhostOwner.Length; ++i)
                {
                    testWorld.ServerWorld.EntityManager.SetComponentData(connectionsOnServer[i+1], new CommandTarget(){ targetEntity = playerEntities[i]});
                    Assert.AreEqual(playerGhostOwner[i].NetworkId, networkIdsOnServer[i+1].Value);
                }

                // TODO：客户端连接恢复尚未自动处理，因此需要手动加入游戏状态
                for (int i = 1; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // 等待服务端 Snapshot 在客户端生成玩家 Ghost
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // 同时在重连客户端设置 CommandTarget 与本地所有者标记
                for (int i = 1; i < clientCount; ++i)
                {
                    var world = testWorld.ClientWorlds[i];
                    var connectionOnClient = testWorld.TryGetSingletonEntity<NetworkId>(world);
                    var playerOnClient = testWorld.TryGetSingletonEntity<HMRemoteInput>(world);
                    Assert.AreNotEqual(playerOnClient, Entity.Null);
                    world.EntityManager.SetComponentData(connectionOnClient, new CommandTarget{targetEntity = playerOnClient});
                    world.EntityManager.AddComponent<GhostOwnerIsLocal>(playerOnClient);
                }

                // 推进 Tick，让 Ghost Collection System 和输入链路完成处理
                for (int i = 0; i < 20; ++i)
                    testWorld.Tick();

                for (int i = 1; i < clientCount; ++i)
                {
                    var setInputSystem = testWorld.ClientWorlds[i].GetExistingSystemManaged<SetInputSystem>();
                    var getInputSystem = testWorld.ClientWorlds[i].GetExistingSystemManaged<GetInputSystem>();
                    Assert.AreEqual(SetInputSystem.TargetEventCount, setInputSystem.SendCounter);
                    Assert.AreEqual(SetInputSystem.TargetEventCount, getInputSystem.ReceiveCounter);
                    // TODO：EventCountValue 应与 ReceiveCounter 相等
                    Assert.Greater(10000, getInputSystem.EventCountValue);
                }

                serverInputSystem = testWorld.ServerWorld.GetExistingSystemManaged<GetInputSystem>();
                Assert.AreEqual((clientCount-1) * SetInputSystem.TargetEventCount, serverInputSystem.ReceiveCounter);
                Assert.Greater(10000, serverInputSystem.EventCountValue);

                // 验证迁移后的 Ghost 数据保持完整
                using var ghostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostOwner>(), ComponentType.ReadOnly<GhostType>(), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<SimpleData>());
                var ghostOwners = ghostQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                var ghostPositions = ghostQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                for (int i = 0; i < clientCount-1; ++i)
                {
                    Assert.AreEqual(i+2, ghostOwners[i].NetworkId);     // 首个客户端也会重连并获得 ID 1，但其玩家 Ghost 在保存 Host 数据时已移除
                    Assert.AreEqual(new float3(i+2, i+3, i+4), ghostPositions[i].Position); // 原首个连接的位置为 (1,2,3)，因此剩余数据从 (2,3,4) 开始
                }
            }
        }


        /// <summary>
        /// 验证服务端 Tick 回退且相同 Tick 的 Snapshot 两次到达客户端时，Prespawn Snapshot Buffer 仍能正确处理
        /// 该测试源自迁移后的一个问题：Prespawn 有时会尝试反序列化超出预期的数据
        /// 原因是 Snapshot Buffer 同时包含迁移前和迁移后的同一 Tick Snapshot
        /// 如果迁移后的 Snapshot 排在迁移前数据之后，旧逻辑可能错误地将其选为 Baseline
        /// 当两个 Baseline 的 ChangeMask 不同时，会进一步导致异常行为
        /// 本测试直接修改 Prespawn Snapshot Buffer，在 Host Migration 期间构造该无效历史状态
        /// </summary>

        [Test]
        public unsafe void MigrationWithPrespawnWithForcedBadSnapshotHistory()
        {
            // 创建带测试更新组件的 Prespawn Ghost
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent), typeof(SomeDataAuthoring));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubSceneWithPrefabs(scene, ScenePath, "subscene", new[] { ghost }, 1);
            SceneManager.SetActiveScene(scene);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem), typeof(IncrementSomeDataSystem));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);

                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());

                testWorld.Connect();
                testWorld.GoInGame();

                // 推进足够多的 Tick，使 Snapshot 数据稳定流转
                testWorld.TickMultiple(64);

                var prespawns = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(Unity.NetCode.Tests.SomeData), typeof(GhostInstance)).ToComponentDataArray<GhostInstance>(Allocator.Temp);

                // 检查客户端实体包含预期组件并且确实是 Prespawn Ghost
                Assert.AreEqual(1, prespawns.Length, "Number of expected prespawns doesn't match.");
                Assert.IsTrue(PrespawnHelper.IsPrespawnGhostId(prespawns[0].ghostId));

                unsafe
                {
                    GetHostMigrationData(testWorld, out var migrationData);

                    // 继续推进模拟，积累后续 Snapshot
                    testWorld.TickMultiple(21); // 21 个 Tick 可以填满 Snapshot Buffer

                    // 获取 Prespawn 实体的 Snapshot Buffer 和序列化布局
                    var ghostCollectionQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                    var ghostCollection = ghostCollectionQuery.GetSingletonEntity();
                    var ghostCollectionPrefabSerializers = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollection);

                    var prespawnGhostData = ghostCollectionPrefabSerializers[prespawns[0].ghostType];

                    // 直接修改客户端 Snapshot Buffer 数据
                    var prespawnEntities = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(Unity.NetCode.Tests.SomeData), typeof(GhostInstance)).ToEntityArray(Allocator.Temp);

                    SnapshotData entitySnapshotData = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SnapshotData>(prespawnEntities[0]);

                    var clientSnapshotBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<SnapshotDataBuffer>(prespawnEntities[0]);
                    byte* snapshotData = (byte*)clientSnapshotBuffer.GetUnsafePtr();


                    void* tempData = UnsafeUtility.Malloc(prespawnGhostData.SnapshotSize, UnsafeUtility.AlignOf<byte>(), Allocator.Temp);
                    int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(prespawnGhostData.ChangeMaskBits);

                    // 反转 Snapshot 数据顺序，并将 LatestIndex 放到 Buffer 中间
                    // 这样迁移后的新 Snapshot 会始终写在旧数据之后
                    int bufferSize = clientSnapshotBuffer.Length / prespawnGhostData.SnapshotSize;
                    for (int i = 0; i < bufferSize / 2; ++i)
                    {
                        int dest = i * prespawnGhostData.SnapshotSize; // Buffer 起始侧
                        int src = (bufferSize - 1 - i) * prespawnGhostData.SnapshotSize; // Buffer 末尾侧
                        uint* changeMask = (uint*)(snapshotData + src + sizeof(uint));

                        // 反转 ChangeMask 位，强制放大错误 Baseline 导致的反序列化问题
                        for (int cm = 0; cm < changeMaskUints; ++cm)
                        {
                            changeMask[cm] ^= 0xFFFFFFFF;
                        }

                        UnsafeUtility.MemCpy(tempData, snapshotData + dest, prespawnGhostData.SnapshotSize); // 暂存起始侧数据
                        UnsafeUtility.MemCpy(snapshotData + dest, snapshotData + src, prespawnGhostData.SnapshotSize); // 将末尾侧复制到起始侧
                        UnsafeUtility.MemCpy(snapshotData + src, tempData, prespawnGhostData.SnapshotSize); // 将原起始侧数据写回末尾侧
                    }

                    UnsafeUtility.Free(tempData, Allocator.Temp);

                    // 将最新 Snapshot 索引设到 Buffer 中间
                    entitySnapshotData.LatestIndex = bufferSize / 2;
                    testWorld.ClientWorlds[0].EntityManager.SetComponentData<SnapshotData>(prespawnEntities[0], entitySnapshotData);

                    // 执行 Host Migration
                    DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                    // 推进 Host Migration System，必须先恢复全部 Ghost 才能处理客户端重连
                    testWorld.TickMultiple(2);

                    // 重新连接客户端
                    var ep = NetworkEndpoint.LoopbackIpv4;
                    ep.Port = 7979;
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                    for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    {
                        testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);
                    }

                    testWorld.TickMultiple(16);

                    // 将客户端连接加入游戏状态
                    for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    {
                        using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                        testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                    }

                    // 推进 Tick，让 Ghost Collection System 完成处理
                    testWorld.TickMultiple(6);

                    // 确认 Host Migration 已成功结束
                    Assert.AreEqual(0, testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HostMigrationInProgress>()).ToEntityArray(Allocator.Temp).Length, "'HostMigrationInProgress' component still exists. Migration failed/timed out.");
                    Assert.AreEqual(0, testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HostMigrationRequest>()).ToEntityArray(Allocator.Temp).Length, "'HostMigrationRequest' component still exists. Migration failed/timed out.");

                    // 继续发送 Snapshot，若仍存在错误应在此期间触发
                    // 随着新数据填满 Snapshot Buffer，旧的异常历史会被覆盖，因此不能再复现问题
                    testWorld.TickMultiple(64);
                }
            }
        }



        [Test]
        public unsafe void MigrationKeepsDynamicGhostIds()
        {
            // 检查被追踪 Ghost 的 ID 与原值一致，并且都在预期的偶数 ID 集合中
            Action<World, string, string> CheckTrackerGhosts = (World world, string worldName, string errorPrefix) =>
            {
                var ghostTrackers = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadWrite<GhostIdAndTickChecker>());
                int[] expectedTrackerGhostIds = { 2, 4, 6, 8 };

                Assert.AreEqual(4, ghostTrackers.CalculateEntityCount(), $"{errorPrefix}: {worldName} World expecting 4 ghosts with tracking data found: {ghostTrackers.CalculateEntityCount()}");

                foreach (var e in ghostTrackers.ToEntityArray(Allocator.Temp))
                {
                    var ghostInstance = world.EntityManager.GetComponentData<GhostInstance>(e);
                    var ghostTracker = world.EntityManager.GetComponentData<GhostIdAndTickChecker>(e);

                    Assert.AreEqual(ghostInstance.ghostId, ghostTracker.originalGhostId, $"{errorPrefix}: {worldName} Ghost {e} has mis-tracked ghostId {ghostInstance.ghostId}:{ghostTracker.originalGhostId}");
                    Assert.AreEqual(ghostInstance.spawnTick, ghostTracker.originalSpawnTick, $"{errorPrefix}: {worldName} Ghost {e} has mis-tracked spawnTick {ghostInstance.spawnTick}:{ghostTracker.originalSpawnTick}");
                    Assert.IsTrue(expectedTrackerGhostIds.Contains(ghostInstance.ghostId), $"{errorPrefix}: {worldName} Ghost has id: {ghostInstance.ghostId} this should be one of 2,4,6,8");
                }
            };

            // 检查迁移动作后生成的 Ghost 数量正确，并从预期的奇数 ID 集合中分配
            // 这能证明迁移前后释放的 ID 都正确归还到空闲列表
            Action<World, string, string> CheckPostMigrationActionGhosts = (World world, string worldName, string errorPrefix) =>
            {
                var postMighrationActionGhosts = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadWrite<CreatedPostHostMigrationAction>());
                int[] expectedPostMigrationmActionGhostIds = { 1, 3, 5, 7 };

                Assert.AreEqual(4, postMighrationActionGhosts.CalculateEntityCount(), $"{errorPrefix}: {worldName} World expecting 4 ghosts post migration action found: {postMighrationActionGhosts.CalculateEntityCount()}");

                foreach (var e in postMighrationActionGhosts.ToEntityArray(Allocator.Temp))
                {
                    var ghostInstance = world.EntityManager.GetComponentData<GhostInstance>(e);

                    Assert.IsTrue(expectedPostMigrationmActionGhostIds.Contains(ghostInstance.ghostId), $"{errorPrefix}: {worldName} Ghost has id: {ghostInstance.ghostId} this should be one of 1,3,5,7");
                }
            };


            const int clientCount = 2;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                // 创建追踪 Ghost 和迁移动作 Ghost 使用的 Prefab
                var trackerPrefabTypes = new ComponentType[1];
                trackerPrefabTypes[0] = ComponentType.ReadOnly<GhostIdAndTickChecker>();
                var postHostMigratioActionPrefabTypes = new ComponentType[1];
                postHostMigratioActionPrefabTypes[0] = ComponentType.ReadOnly<CreatedPostHostMigrationAction>();

                for (int i = 0; i < testWorld.ClientWorlds.Length; i++)
                {
                    CreatePrefab(testWorld.ClientWorlds[i].EntityManager, "GhostIdTracker", trackerPrefabTypes);
                    CreatePrefab(testWorld.ClientWorlds[i].EntityManager, "PostHostMigrationAction", postHostMigratioActionPrefabTypes);
                }
                var serverEntityManager = testWorld.ServerWorld.EntityManager;
                serverEntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                var trackerPrefab = CreatePrefab(serverEntityManager, "GhostIdTracker", trackerPrefabTypes);
                var postHostMigrationActionPrefab = CreatePrefab(serverEntityManager, "PostHostMigrationAction", postHostMigratioActionPrefabTypes);

                testWorld.Connect(maxSteps: 10);
                testWorld.GoInGame();

                testWorld.TickMultiple(4);

                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                Assert.AreEqual(2, serverPrefabs.Length);

                // 确认当前尚未生成 Ghost，保证测试完全控制 Ghost ID 分配
                var ghostEntitiesQuery = serverEntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                Assert.AreEqual(0, ghostEntitiesQuery.ToEntityArray(Allocator.Temp).Length, "The test makes assumtions that there are no othter ghosts created so the test has complete control over the ghostIds.");

                // 生成 8 个 Ghost
                for (int i = 0; i < 8; ++i)
                    serverEntityManager.Instantiate(trackerPrefab);

                testWorld.TickMultiple(4);

                // 将 Ghost ID 和 SpawnTick 写入追踪组件，并删除所有奇数 ID 的 Ghost
                var serverGhostTrackers = serverEntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadWrite<GhostIdAndTickChecker>());
                var ecb = new EntityCommandBuffer(Allocator.Temp);
                foreach (var e in serverGhostTrackers.ToEntityArray(Allocator.Temp))
                {
                    var ghostInstance = serverEntityManager.GetComponentData<GhostInstance>(e);

                    if (ghostInstance.ghostId % 2 == 0) // 偶数 Ghost ID
                    {
                        // 保留并记录原始标识
                        serverEntityManager.SetComponentData(e, new GhostIdAndTickChecker() { originalGhostId = ghostInstance.ghostId, originalSpawnTick = ghostInstance.spawnTick });
                    }
                    else // 销毁奇数 ID 的 Ghost
                    {
                        ecb.DestroyEntity(e);
                    }
                }

                ecb.Playback(serverEntityManager);

                // 推进 Tick，让客户端同步这些变更
                testWorld.TickMultiple(6);

                // 采集 Host Migration 数据
                GetHostMigrationData(testWorld, out var migrationData);

                // 再生成 4 个 Ghost，应复用空闲 ID 1、3、5、7
                for (int i = 0; i < 4; ++i)
                    serverEntityManager.Instantiate(postHostMigrationActionPrefab);

                // 推进 Tick，让客户端同步这些变更
                testWorld.TickMultiple(6);

                CheckAllWorlds(testWorld, "Pre Migration", new List<Action<World, string, string>> { CheckTrackerGhosts, CheckPostMigrationActionGhosts });

                // 销毁当前服务端并创建新的服务端 World
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                serverEntityManager = testWorld.ServerWorld.EntityManager;

                // 恢复 Prefab 和 Ghost Collection，真实迁移流程通常由 SubScene 加载完成
                // 以相反顺序创建 Prefab，确保新旧服务端的 GhostType 索引不一致
                CreatePrefab(serverEntityManager, "GhostIdTracker", trackerPrefabTypes);
                postHostMigrationActionPrefab = CreatePrefab(serverEntityManager, "PostHostMigrationAction", postHostMigratioActionPrefabTypes);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);
                testWorld.TickMultiple(8);

                // TODO：客户端连接恢复尚未自动处理，因此需要手动加入游戏状态
                for (int i = 0; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // 推进 Tick，让 Ghost Collection System 完成处理
                testWorld.TickMultiple(6);

                // 迁移后再生成 4 个 Ghost，仍应复用空闲 ID 1、3、5、7
                for (int i = 0; i < 4; ++i)
                    serverEntityManager.Instantiate(postHostMigrationActionPrefab);

                // 推进发送 System 并完成 Ghost ID 分配
                testWorld.TickMultiple(6);

                // 检查迁移 Ghost 保留原 ID，新 Ghost 正确复用空闲 ID
                CheckAllWorlds(testWorld, "Post Migration", new List<Action<World, string, string>> { CheckTrackerGhosts, CheckPostMigrationActionGhosts });
            }
        }


        [Test]
        public unsafe void MigrationKeepsPrespawnGhostIds()
        {
            int expectedGhosts = 0;
            Action<World, string, string> CheckPreSpawnGhostsAreCorrect = (World world, string worldName, string errorPrefix) =>
            {
                var ghostTrackers = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadWrite<GhostIdAndTickChecker>());
                Assert.AreEqual(expectedGhosts, ghostTrackers.CalculateEntityCount(), $"{errorPrefix}: {worldName} World expecting {expectedGhosts} ghosts with tracking data found: {ghostTrackers.CalculateEntityCount()}");

                foreach (var e in ghostTrackers.ToEntityArray(Allocator.Temp))
                {
                    var ghostInstance = world.EntityManager.GetComponentData<GhostInstance>(e);
                    var ghostTracker = world.EntityManager.GetComponentData<GhostIdAndTickChecker>(e);

                    Assert.AreEqual(ghostInstance.ghostId, ghostTracker.originalGhostId, $"{errorPrefix}: {worldName} Ghost {e} has mis-tracked ghostId {ghostInstance.ghostId}:{ghostTracker.originalGhostId}");
                    Assert.AreEqual(ghostInstance.spawnTick, ghostTracker.originalSpawnTick, $"{errorPrefix}: {worldName} Ghost {e} has mis-tracked spawnTick {ghostInstance.spawnTick}:{ghostTracker.originalSpawnTick}");
                }
            };


            // 创建包含 3 个 Prespawn 场景的服务端，每个场景放置一个带追踪组件的 Ghost
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "Ghost", typeof(GhostAuthoringComponent), typeof(GhostIdAndTickCheckerAuthoring));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "ParentScene");
            var subscene1 = SubSceneHelper.CreateSubSceneWithPrefabs(scene, ScenePath, "SubScene_1", new[] { ghost }, 1);
            var subscene2 = SubSceneHelper.CreateSubSceneWithPrefabs(scene, ScenePath, "SubScene_2", new[] { ghost }, 1);
            var subscene3 = SubSceneHelper.CreateSubSceneWithPrefabs(scene, ScenePath, "SubScene_3", new[] { ghost }, 1);
            SceneManager.SetActiveScene(scene);

            const int clientCount = 2;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);

                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());

                testWorld.Connect(maxSteps: 10);
                testWorld.GoInGame();

                testWorld.TickMultiple(8);

                var serverEntityManager = testWorld.ServerWorld.EntityManager;
                var serverGhostTrackers = serverEntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadWrite<GhostIdAndTickChecker>());
                foreach (var e in serverGhostTrackers.ToEntityArray(Allocator.Temp))
                {
                    var ghostInstance = serverEntityManager.GetComponentData<GhostInstance>(e);
                    serverEntityManager.SetComponentData(e, new GhostIdAndTickChecker() { originalGhostId = ghostInstance.ghostId, originalSpawnTick = ghostInstance.spawnTick });
                }

                testWorld.TickMultiple(8);

                // 此时应加载 3 个 SubScene
                Assert.AreEqual(3, serverEntityManager.CreateEntityQuery(ComponentType.ReadOnly<SceneReference>()).ToEntityArray(Allocator.Temp).Length);

                // World 初始化完成后，3 个追踪 Ghost 都应匹配原始标识
                expectedGhosts = 3;
                CheckAllWorlds(testWorld, "Pre Migration 3 scenes", new List<Action<World, string, string>> { CheckPreSpawnGhostsAreCorrect });


                // 当前有 ID 为 1、2、3 的三个 Prespawn，卸载包含 ID 2 的场景以构造不连续 ID
                foreach( var entityInScene in serverEntityManager.CreateEntityQuery(ComponentType.ReadOnly<SceneSection>(), ComponentType.ReadOnly<GhostInstance>()).ToEntityArray(Allocator.Temp) )
                {
                    if (serverEntityManager.GetComponentData<GhostInstance>(entityInScene).ghostId == PrespawnHelper.MakePrespawnGhostId(2))
                    {
                        // 测试无法控制 Ghost ID 的初始分配顺序，因此明确卸载中间 ID 所在场景
                        // 这样迁移数据中的 Prespawn ID 间隔会大于 1
                        SceneSystem.UnloadScene(testWorld.ServerWorld.Unmanaged, serverEntityManager.GetSharedComponent<SceneSection>(entityInScene).SceneGUID, SceneSystem.UnloadParameters.DestroyMetaEntities);
                        break;
                    }
                }

                testWorld.TickMultiple(16);

                Assert.AreEqual(2, serverEntityManager.CreateEntityQuery(ComponentType.ReadOnly<SceneReference>()).ToEntityArray(Allocator.Temp).Length);

                // 卸载后应只剩 2 个 Ghost
                expectedGhosts = 2;
                CheckAllWorlds(testWorld, "Pre Migration 2 scenes", new List<Action<World, string, string>> { CheckPreSpawnGhostsAreCorrect });

                // 执行 Host Migration
                GetHostMigrationData(testWorld, out var migrationData);
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                serverEntityManager = testWorld.ServerWorld.EntityManager;

                testWorld.TickMultiple(2);

                // 重新连接客户端
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);

                testWorld.TickMultiple(8);

                // TODO：客户端连接恢复尚未自动处理，因此需要手动加入游戏状态
                for (int i = 0; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                testWorld.TickMultiple(32);

                // 迁移后仍应只加载 2 个场景
                Assert.AreEqual(2, serverEntityManager.CreateEntityQuery(ComponentType.ReadOnly<SceneReference>()).ToEntityArray(Allocator.Temp).Length);

                // 迁移后应保留 2 个 Ghost，且其 ID 与迁移前一致
                expectedGhosts = 2;
                CheckAllWorlds(testWorld, "Post Migration 2 scenes", new List<Action<World, string, string>> { CheckPreSpawnGhostsAreCorrect });
            }
        }

        /// <summary>
        /// Host Migration 后，原会话中的客户端重连时应保留原 NetworkId
        /// </summary>
        [Test]
        public void ClientsKeepIDsAfterMigration()
        {
            // 在重连过程和迁移完成后分别加入新客户端，验证它们获得连续的下一个 NetworkId

            const int k_initialClientCount = 4;
            const int k_extraClients = 2;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, k_initialClientCount + k_extraClients);

                for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                {
                    testWorld.ClientWorlds[i].EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                    CreatePrefabWithInputs(testWorld.ClientWorlds[i].EntityManager);
                }
                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefabWithInputs(testWorld.ServerWorld.EntityManager);

                // 启动初始客户端连接
                testWorld.StartSeverListen();
                for ( int i=0; i< k_initialClientCount; ++i )
                {
                    testWorld.ConnectSingleClientWorld(i);
                }

                // 将初始连接加入游戏状态
                testWorld.GoInGame(testWorld.ServerWorld);
                for (int i = 0; i < k_initialClientCount; ++i)
                {
                    testWorld.GoInGame(testWorld.ClientWorlds[i]);
                }

                // 推进 Tick，让 Ghost Collection 完成初始化
                testWorld.TickMultiple(4);

                var connectionsOnServerQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                var networkIdsOnServer = connectionsOnServerQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
                var connectionsOnServer = connectionsOnServerQuery.ToEntityArray(Allocator.Temp);

                testWorld.TickMultiple(4);

                // 保存迁移前的 ConnectionUniqueId 与 NetworkId 映射
                var preMigrationNetworkIds = new NativeHashMap<uint, int>(testWorld.ClientWorlds.Length,Allocator.Temp);
                GetUniqueAndNetworkIds( ref preMigrationNetworkIds, testWorld, k_initialClientCount);

                // 此处在同一 Tick 保存迁移数据并恢复到新服务端，不模拟定期上传外部服务产生的延迟
                testWorld.ServerWorld.EntityManager.CompleteAllTrackedJobs();
                GetHostMigrationData(testWorld, out var migrationData);

                // 销毁当前服务端并创建新的服务端 World
                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                // 推进 Host Migration System，必须先恢复全部 Ghost 才能处理客户端重连
                testWorld.TickMultiple(2);

                // 逐个重连客户端，以刻意打乱 NetworkId 分配请求顺序
                // 若批量重连，它们会按上方相同顺序一起处理，无法覆盖该边界情况
                testWorld.StartSeverListen();

                // 在旧客户端重连中途插入新客户端，验证它不会破坏原 ID 恢复顺序
                testWorld.ConnectSingleClientWorld(3);
                testWorld.ConnectSingleClientWorld(2);

                // 插入一个新客户端
                testWorld.ConnectSingleClientWorld(4);
                testWorld.GoInGame(testWorld.ClientWorlds[4]);

                testWorld.ConnectSingleClientWorld(1);
                testWorld.ConnectSingleClientWorld(0);

                testWorld.TickMultiple(4);

                CheckWorldNetworkId(testWorld, 4, 5);

                var postMigrationNetworkIds = new NativeHashMap<uint, int>(testWorld.ClientWorlds.Length, Allocator.Temp);
                GetUniqueAndNetworkIds(ref postMigrationNetworkIds, testWorld, k_initialClientCount);

                // 确认旧客户端迁移前后的 NetworkId 一致
                foreach( var postIds in postMigrationNetworkIds)
                {
                    Assert.IsTrue(preMigrationNetworkIds.ContainsKey(postIds.Key), $"UniqueId {postIds.Key} in post migration clients list put not pre migration clients list.");
                    Assert.AreEqual(postIds.Value, preMigrationNetworkIds[postIds.Key], $"NetworkId mismatch: Client with uniqueid:{postIds.Key} has networkId {preMigrationNetworkIds[postIds.Key]} pre migration and {postIds.Value} post migration.");
                }

                // 迁移完成后再加入新客户端，验证其获得当前最大值加 1 的 NetworkId
                testWorld.ConnectSingleClientWorld(5);
                testWorld.GoInGame(testWorld.ClientWorlds[5]);

                CheckWorldNetworkId(testWorld, 5, 6);
            }
        }

        static void GetUniqueAndNetworkIds( ref NativeHashMap<uint, int> uniqueIdToNetworkIDMap, NetCodeTestWorld testWorld, int numClients)
        {
            for (int cc = 0; cc < numClients; ++cc)
            {
                var connectionOnClient = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[cc]);
                var uniqueconnectionOnClient = testWorld.TryGetSingletonEntity<ConnectionUniqueId>(testWorld.ClientWorlds[cc]);
                var nid = testWorld.ClientWorlds[cc].EntityManager.GetComponentData<NetworkId>(connectionOnClient);
                var uid = testWorld.ClientWorlds[cc].EntityManager.GetComponentData<ConnectionUniqueId>(uniqueconnectionOnClient);

                uniqueIdToNetworkIDMap.Add(uid.Value, nid.Value);
            }
        }

        static void CheckWorldNetworkId(NetCodeTestWorld testWorld, int clientWorldIndex, int expectedNetworkId )
        {
            var connectionOnClient = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[clientWorldIndex]);
            var nid = testWorld.ClientWorlds[clientWorldIndex].EntityManager.GetComponentData<NetworkId>(connectionOnClient);

            Assert.AreEqual(expectedNetworkId, nid.Value, $"Client given unexpected id, Client {testWorld.ClientWorlds[clientWorldIndex].Name} at index {clientWorldIndex} was expecting an id of {expectedNetworkId}, has been assigned an id of {nid.Value}.");
        }


        [Test]
        public unsafe void GhostSpawnedOnSameFrameAsMigrationHasValidGhostType()
        {
            int clientCount = 2;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(ServerHostMigrationSystem));
                testWorld.CreateWorlds(true, clientCount);

                // 创建两种 Ghost Prefab，并在 Host Migration 前一帧生成第二种类型的 Ghost
                for (int i = 0; i < clientCount; ++i)
                {
                    CreatePrefab(testWorld.ClientWorlds[i].EntityManager);
                    CreatePrefabTypeTwo(testWorld.ClientWorlds[i].EntityManager);
                }

                testWorld.ServerWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
                CreatePrefab(testWorld.ServerWorld.EntityManager);
                CreatePrefabTypeTwo(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps: 10);
                testWorld.GoInGame();

                testWorld.TickMultiple(4);

                // 生成第二种 Prefab 类型的 Ghost
                var serverPrefabs = testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld);
                Assert.AreEqual(2, serverPrefabs.Length);

                testWorld.ServerWorld.EntityManager.Instantiate(serverPrefabs[1].GhostPrefab);

                // 迁移采集前，该 Ghost 尚未经过发送 System 分配有效 Ghost ID 和 GhostType
                {
                    var ghostInstanceQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                    var ghostEntities = ghostInstanceQuery.ToEntityArray(Allocator.Temp);
                    Assert.AreEqual(1, ghostEntities.Length);
                    var ghostInstance = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ghostEntities[0]);
                    Assert.AreEqual(0, ghostInstance.ghostId);
                    Assert.AreEqual(0, ghostInstance.ghostType);
                }

                var migrationConfig = testWorld.GetSingletonRW<HostMigrationConfig>(testWorld.ServerWorld);
                migrationConfig.ValueRW.ServerUpdateInterval = 0.0f; // 立即采集迁移数据

                GetHostMigrationData(testWorld, out var migrationData);

                // 采集迁移数据后，该 Ghost 应已获得有效 Ghost ID 和 GhostType
                {
                    var ghostInstanceQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                    var ghostEntities = ghostInstanceQuery.ToEntityArray(Allocator.Temp);
                    Assert.AreEqual(1, ghostEntities.Length);
                    var ghostInstance = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ghostEntities[0]);
                    Assert.AreNotEqual(0, ghostInstance.ghostId);
                    Assert.AreNotEqual(0, ghostInstance.ghostType);
                }

                DisconnectServerAndCreateNewServerWorld(testWorld, ref migrationData);

                using var hostMigrationDataQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HostMigrationStorage>());
                var hostMigrationData = hostMigrationDataQuery.ToComponentDataArray<HostMigrationStorage>(Allocator.Temp);
                Assert.AreEqual(1, hostMigrationData.Length);

                CreatePrefab(testWorld.ServerWorld.EntityManager);
                CreatePrefabTypeTwo(testWorld.ServerWorld.EntityManager);

                testWorld.Connect(maxSteps: 10);

                // TODO：客户端连接恢复尚未自动处理，因此需要手动加入游戏状态
                for (int i = 0; i < clientCount; ++i)
                {
                    using var clientConnectionQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    testWorld.ClientWorlds[i].EntityManager.AddComponent<NetworkStreamInGame>(clientConnectionQuery.GetSingletonEntity());
                }

                // 推进 Tick，让 Ghost Collection System 完成处理
                testWorld.TickMultiple(2);

                // 迁移后应恢复该 Ghost，并保留非零 Ghost ID 和 GhostType
                {
                    var ghostInstanceQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
                    var ghostEntities = ghostInstanceQuery.ToEntityArray(Allocator.Temp);
                    Assert.AreEqual(1, ghostEntities.Length);
                    var ghostInstance = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ghostEntities[0]);
                    Assert.AreNotEqual(0, ghostInstance.ghostId);
                    Assert.AreNotEqual(0, ghostInstance.ghostType);
                }
            }
        }

        static void CheckGhostAndTrackersMatch(World world, string worldName, string errorPrefix, int expectedGhosts)
        {
            var ghostTrackers = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadWrite<GhostIdAndTickChecker>());

            Assert.AreEqual(expectedGhosts, ghostTrackers.CalculateEntityCount(), $"{errorPrefix}: {worldName} World expecting {expectedGhosts} ghosts with tracking data found: {ghostTrackers.CalculateEntityCount()}");

            foreach (var e in ghostTrackers.ToEntityArray(Allocator.Temp))
            {
                var ghostInstance = world.EntityManager.GetComponentData<GhostInstance>(e);
                var ghostTracker = world.EntityManager.GetComponentData<GhostIdAndTickChecker>(e);

                Assert.AreEqual(ghostInstance.ghostId, ghostTracker.originalGhostId, $"{errorPrefix}: {worldName} Ghost {e} has mis-tracked ghostId {ghostInstance.ghostId}:{ghostTracker.originalGhostId}");
                Assert.AreEqual(ghostInstance.spawnTick, ghostTracker.originalSpawnTick, $"{errorPrefix}: {worldName} Ghost {e} has mis-tracked spawnTick {ghostInstance.spawnTick}:{ghostTracker.originalSpawnTick}");
            }
        }

        static void CheckAllWorlds(NetCodeTestWorld testWorld, string checkPrefix, List<Action<World, string, string>> checks )
        {
            foreach (var check in checks)
            {
                check(testWorld.ServerWorld, "Server", checkPrefix);
            }

            for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
            {
                foreach (var check in checks)
                {
                    check(testWorld.ClientWorlds[i], "Client", checkPrefix);
                }
            }
        }

        static void GetHostMigrationData(NetCodeTestWorld testWorld, out NativeList<byte> migrationData)
        {
            var currentTime = testWorld.ServerWorld.Time.ElapsedTime;
            var migrationStats = testWorld.GetSingleton<HostMigrationStats>(testWorld.ServerWorld);
            var timeout = currentTime + 10;
            while (migrationStats.LastDataUpdateTime < currentTime)
            {
                testWorld.Tick();
                migrationStats = testWorld.GetSingleton<HostMigrationStats>(testWorld.ServerWorld);
                if (testWorld.ServerWorld.Time.ElapsedTime > timeout)
                    Assert.Fail("Timeout while waiting for host migration data update");
            }
            migrationData = new NativeList<byte>(0, Allocator.Temp);
            HostMigrationData.Get(testWorld.ServerWorld, ref migrationData);
        }

        static Entity CreateHostDataPrefab(EntityManager entityManager)
        {
            var prefab = entityManager.CreateEntity();
            entityManager.AddComponent<HostOnlyData>(prefab);
            entityManager.AddBuffer<HostOnlyBuffer>(prefab);

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "HostDataPrefab",
                Importance = 0,
                SupportedGhostModes = GhostModeMask.Interpolated,
                DefaultGhostMode = GhostMode.Interpolated,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });

            return prefab;
        }

        static Entity CreatePrefab( EntityManager entityManager, FixedString64Bytes name, ComponentType[] components, bool addTransform = false )
        {
            var prefab = entityManager.CreateEntity();
            if ( addTransform )
                entityManager.AddComponentData(prefab, LocalTransform.Identity);

            foreach ( var c in components )
            {
                entityManager.AddComponent(prefab, c);
            }

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = name,
                Importance = 0,
                SupportedGhostModes = GhostModeMask.Interpolated,
                DefaultGhostMode = GhostMode.Interpolated,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });

            return prefab;
        }

        static Entity CreatePrefab(EntityManager entityManager)
        {
            var prefab = entityManager.CreateEntity();
            entityManager.AddComponentData(prefab, LocalTransform.Identity);
            entityManager.AddComponent<GhostOwner>(prefab);
            entityManager.AddBuffer<SomeBuffer>(prefab);
            entityManager.AddBuffer<AnotherBuffer>(prefab);

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "PlayerPrefab",
                Importance = 0,
                SupportedGhostModes = GhostModeMask.All,
                DefaultGhostMode = GhostMode.OwnerPredicted,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });

            return prefab;
        }

        static Entity CreatePrefabWithOnlyComponents(EntityManager entityManager)
        {
            var prefab = entityManager.CreateEntity();
            entityManager.AddComponent<SimpleData>(prefab);
            entityManager.AddComponent<MoreData>(prefab);

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "PrefabWithOnlyComponents",
                Importance = 0,
                SupportedGhostModes = GhostModeMask.All,
                DefaultGhostMode = GhostMode.Interpolated,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });

            return prefab;
        }


        static Entity CreatePrefabTypeTwo(EntityManager entityManager)
        {
            var prefab = entityManager.CreateEntity();
            entityManager.AddComponentData(prefab, LocalTransform.Identity);
            entityManager.AddComponent<SimpleData>(prefab);
            entityManager.AddBuffer<SomeBuffer>(prefab); // 空 Buffer

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "PlayerPrefabTypeTwo",
                Importance = 0,
                SupportedGhostModes = GhostModeMask.Interpolated,
                DefaultGhostMode = GhostMode.Interpolated,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });

            return prefab;
        }

        static Entity CreatePrefabWithEnableable(EntityManager entityManager)
        {
            var prefab = entityManager.CreateEntity();
            entityManager.AddComponentData(prefab, LocalTransform.Identity);
            entityManager.AddComponent<SomeEnableable>(prefab);
            entityManager.AddComponent<SimpleData>(prefab);
            entityManager.AddBuffer<SomeBuffer>(prefab);

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "PlayerPrefabWithEnableable",
                Importance = 0,
                SupportedGhostModes = GhostModeMask.All,
                DefaultGhostMode = GhostMode.Predicted,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });

            return prefab;
        }

        static Entity CreatePrefabWithInputs(EntityManager entityManager)
        {
            var prefab = entityManager.CreateEntity();
            entityManager.AddComponentData(prefab, LocalTransform.Identity);
            entityManager.AddComponent<GhostOwner>(prefab);
            entityManager.AddComponent<HMRemoteInput>(prefab);
            entityManager.AddComponent<InputBufferData<HMRemoteInput>>(prefab);
            entityManager.AddComponent<AutoCommandTarget>(prefab);
            entityManager.SetComponentData(prefab, new AutoCommandTarget(){ Enabled = true });
            entityManager.AddComponent<SimpleData>(prefab);

            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "PlayerPrefabWithInputs",
                Importance = 0,
                SupportedGhostModes = GhostModeMask.All,
                DefaultGhostMode = GhostMode.OwnerPredicted,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });

            return prefab;
        }

        static void WaitForClientDisconnect(NetCodeTestWorld testWorld, int clientCount)
        {
            for (int i = 0; i < 2; ++i)
                testWorld.Tick();
            for (int i = 0; i < clientCount; ++i)
            {
                using var networkIdQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                Assert.AreEqual(0, networkIdQuery.CalculateEntityCount());
            }
        }

        /// <summary>
        /// 生成服务端所有的 Ghost，并写入指定测试数据
        /// </summary>
        static void CreateServerGhosts(int serverGhostCount, NetCodeTestWorld testWorld, Entity prefab, int startIndex = 0)
        {
            for (int i = startIndex; i < startIndex + serverGhostCount; ++i)
            {
                var serverGhostEntity = testWorld.ServerWorld.EntityManager.Instantiate(prefab);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverGhostEntity, new SimpleData() { IntValue = 100 + i, FloatValue = 100f + i, QuaternionValue = Quaternion.Euler(1,2,3), StringValue = $"HelloWorldHelloWorldHelloWorld" });
                testWorld.ServerWorld.EntityManager.SetComponentData(serverGhostEntity, new MoreData() { IntValue = 1000 + i, FloatValue = 1000f + i});
            }
        }

        /// <summary>
        /// 在服务端为每个客户端添加玩家 Ghost，并将所有者设为对应连接
        /// </summary>
        static void CreatePlayerGhosts(int clientCount, NetCodeTestWorld testWorld, Entity prefab)
        {
            for (int i = 0; i < clientCount; ++i)
            {
                var playerEntity = testWorld.ServerWorld.EntityManager.Instantiate(prefab);
                testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, new GhostOwner() { NetworkId = i+1 });
                var beforePosition = new LocalTransform() { Position = new float3(i+1, i+2, i+3) };
                testWorld.ServerWorld.EntityManager.SetComponentData(playerEntity, beforePosition);
                var someBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<SomeBuffer>(playerEntity);
                someBuffer.Add(new SomeBuffer() { Value = i+100 });
                someBuffer.Add(new SomeBuffer() { Value = i+200 });
                someBuffer.Add(new SomeBuffer() { Value = i+300 });
                someBuffer.Add(new SomeBuffer() { Value = i+400 });
                var anotherBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<AnotherBuffer>(playerEntity);
                anotherBuffer.Add(new AnotherBuffer() { ValueOne = i+1000, ValueTwo = i+2000 });
                anotherBuffer.Add(new AnotherBuffer() { ValueOne = i+3000, ValueTwo = i+4000 });
            }
        }

        static void ValidateServerGhosts(int serverGhostCount, NetCodeTestWorld testWorld)
        {
            using var serverGhostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SimpleData>(), ComponentType.ReadOnly<MoreData>());
            Assert.AreEqual(serverGhostCount, serverGhostQuery.CalculateEntityCount());
            var someData = serverGhostQuery.ToComponentDataArray<SimpleData>(Allocator.Temp);
            var moreData = serverGhostQuery.ToComponentDataArray<MoreData>(Allocator.Temp);
            for (int i = 0; i < serverGhostCount - 1; ++i)
            {
                Assert.AreEqual(100f + i, someData[i].FloatValue);
                Assert.AreEqual(100 + i, someData[i].IntValue);
                Assert.AreEqual(Quaternion.Euler(1,2,3), someData[i].QuaternionValue);
                Assert.AreEqual("HelloWorldHelloWorldHelloWorld", someData[i].StringValue);
                Assert.AreEqual(new MoreData(){ IntValue = 1000 + i, FloatValue = 1000f + i }, moreData[i]);
            }
        }

        /// <summary>
        /// 验证玩家 Ghost 的生成结果正确
        /// </summary>
        /// <param name="skipHostOwnedPlayer">Host 所有的玩家未包含在迁移数据中时，跳过该玩家并相应调整索引和 ID</param>
        static void ValidatePlayerGhosts(int count, NetCodeTestWorld testWorld, bool skipHostOwnedPlayer = true)
        {
            using var ghostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostOwner>(), ComponentType.ReadOnly<GhostType>(), ComponentType.ReadOnly<LocalTransform>());
            Assert.AreEqual(count, ghostQuery.CalculateEntityCount());
            var ghostOwners = ghostQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            var ghostPositions = ghostQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var ghostEntities = ghostQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < count; ++i)
            {
                int expectedNetworkId = i + 1;
                if (skipHostOwnedPlayer)
                    expectedNetworkId = i + 2;
                Assert.AreEqual(expectedNetworkId, ghostOwners[i].NetworkId);
                int nextIndex = i;
                if (skipHostOwnedPlayer)
                    nextIndex = i+1;
                Assert.AreEqual(new float3(nextIndex+1, nextIndex+2, nextIndex+3), ghostPositions[i].Position);
                var someBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<SomeBuffer>(ghostEntities[i]);
                Assert.AreEqual(4, someBuffer.Length);
                Assert.AreEqual(100+nextIndex, someBuffer[0].Value);
                Assert.AreEqual(200+nextIndex, someBuffer[1].Value);
                Assert.AreEqual(300+nextIndex, someBuffer[2].Value);
                Assert.AreEqual(400+nextIndex, someBuffer[3].Value);
                var anotherBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<AnotherBuffer>(ghostEntities[i]);
                Assert.AreEqual(2, anotherBuffer.Length);
                Assert.AreEqual(1000+nextIndex, anotherBuffer[0].ValueOne);
                Assert.AreEqual(2000+nextIndex, anotherBuffer[0].ValueTwo);
                Assert.AreEqual(3000+nextIndex, anotherBuffer[1].ValueOne);
                Assert.AreEqual(4000+nextIndex, anotherBuffer[1].ValueTwo);
            }
        }
    }
}
