#pragma warning disable CS0618 // 禁用 Entities.ForEach 的过时警告
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Transforms;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal struct Data : IComponentData
    {
        [GhostField]
        public int Value;
    }

    [GhostComponent(SendDataForChildEntity = true)]
    internal struct ChildData : IComponentData
    {
        [GhostField]
        public int Value;
    }

    internal class DataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent<Data>(entity);
        }
    }

    internal class PredictedGhostDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostOwner());
            baker.AddComponent(entity, new Data());
            baker.AddComponent(entity, new EnableableComponent_0());
            baker.AddComponent(entity, new EnableableComponent_1());
            baker.AddComponent(entity, new EnableableComponent_2());
            baker.AddBuffer<EnableableBuffer_0>(entity);
            baker.AddBuffer<EnableableBuffer_1>(entity);
        }
    }

    internal class ChildDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new ChildData());
            baker.AddComponent(entity, new ChildOnlyComponent_3());
            baker.AddBuffer<EnableableBuffer>(entity);
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class UpdateDataSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((ref Data data) =>
            {
                data.Value++;
            }).Run();
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSpawnClassificationSystemGroup))]
    [UpdateAfter(typeof(GhostSpawnClassificationSystem))]
    internal partial class TestSpawnClassificationSystem : SystemBase
    {
        // 记录已由此分类系统处理的实体
        public NativeList<Entity> PredictedEntities;
        protected override void OnCreate()
        {
            RequireForUpdate<GhostSpawnQueue>();
            RequireForUpdate<PredictedGhostSpawnList>();
            PredictedEntities = new NativeList<Entity>(5,Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            PredictedEntities.Dispose();
        }

        protected override void OnUpdate()
        {
            var spawnListEntity = SystemAPI.GetSingletonEntity<PredictedGhostSpawnList>();
            var spawnListFromEntity = GetBufferLookup<PredictedGhostSpawn>();
            var predictedEntities = PredictedEntities;
            Entities
                .WithAll<GhostSpawnQueue>()
                .ForEach((DynamicBuffer<GhostSpawnBuffer> ghosts) =>
                {
                    var spawnList = spawnListFromEntity[spawnListEntity];
                    for (int i = 0; i < ghosts.Length; ++i)
                    {
                        var ghost = ghosts[i];
                        if (ghost.SpawnType != GhostSpawnBuffer.Type.Predicted || ghost.HasClassifiedPredictedSpawn || ghost.PredictedSpawnEntity != Entity.Null)
                            continue;

                        // 只分类列表中的第一项，其余项交给默认系统处理
                        // 此处不检查 Spawn Tick 等匹配条件
                        if (spawnList.Length > 1)
                        {
                            if (ghost.GhostType == spawnList[0].ghostType)
                            {
                                ghost.PredictedSpawnEntity = spawnList[0].entity;
                                ghost.HasClassifiedPredictedSpawn = true;
                                spawnList.RemoveAtSwapBack(0);
                                predictedEntities.Add(ghost.PredictedSpawnEntity);
                                ghosts[i] = ghost;
                                break;
                            }
                        }
                    }
                }).Run();
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostReceiveSystem))]
    unsafe partial struct VerifyInitialization : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.Enabled = false;
        }
        public void OnUpdate(ref SystemState state)
        {
            // 在此解码快照数据并检查初始化结果
            var clientEntity = SystemAPI.GetSingletonEntity<GhostInstance>();
            var prefabType = SystemAPI.GetSingletonBuffer<GhostCollectionPrefab>()[0];
            var clientEntity2 = state.EntityManager.Instantiate(prefabType.GhostPrefab);
            var deserializeHelper = new GhostDeserializeHelper(ref state,
                SystemAPI.GetSingletonEntity<GhostCollection>(), clientEntity, 0);
            var ghostComponentCollection = SystemAPI.GetSingletonBuffer<GhostCollectionComponentType>();
            DynamicTypeList.PopulateList(ref state, ghostComponentCollection, false, ref deserializeHelper.ghostChunkComponentTypes);

            var info = state.EntityManager.GetStorageInfo(clientEntity2);
            deserializeHelper.CopySnapshotToEntity(info);
            var linkedEntities = state.EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
            var linkedEntities2 = state.EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity2);
            Assert.AreEqual(
                state.EntityManager.GetComponentData<GhostOwner>(clientEntity).NetworkId,
                state.EntityManager.GetComponentData<GhostOwner>(clientEntity2).NetworkId);
            Assert.AreEqual(
                state.EntityManager.GetComponentData<Data>(clientEntity).Value,
                state.EntityManager.GetComponentData<Data>(clientEntity2).Value);
            Assert.AreEqual(
                state.EntityManager.GetComponentData<EnableableComponent_0>(clientEntity).value,
                state.EntityManager.GetComponentData<EnableableComponent_0>(clientEntity2).value);
            Assert.AreEqual(
                state.EntityManager.GetComponentData<EnableableComponent_1>(clientEntity).value,
                state.EntityManager.GetComponentData<EnableableComponent_1>(clientEntity2).value);
            Assert.AreEqual(
                state.EntityManager.GetComponentData<EnableableComponent_2>(clientEntity).value,
                state.EntityManager.GetComponentData<EnableableComponent_2>(clientEntity2).value);
            {
                var b1 = state.EntityManager.GetBuffer<EnableableBuffer_0>(clientEntity);
                var b2 = state.EntityManager.GetBuffer<EnableableBuffer_0>(clientEntity2);
                Assert.AreEqual(b1.Length, b2.Length);
                for (int b = 0; b < b1.Length; b++)
                {
                    Assert.AreEqual(b1[b].value, b2[b].value);
                }
            }
            {
                var b1 = state.EntityManager.GetBuffer<EnableableBuffer_1>(clientEntity);
                var b2 = state.EntityManager.GetBuffer<EnableableBuffer_1>(clientEntity2);
                Assert.AreEqual(b1.Length, b2.Length);
                for (int b = 0; b < b1.Length; b++)
                {
                    Assert.AreEqual(b1[b].value, b2[b].value);
                }
            }
            for (int i = 1; i < linkedEntities.Length; ++i)
            {
                Assert.AreEqual(
                    state.EntityManager.GetComponentData<ChildData>(linkedEntities[i].Value).Value,
                    state.EntityManager.GetComponentData<ChildData>(linkedEntities2[i].Value).Value);
                Assert.AreEqual(
                    state.EntityManager.GetComponentData<ChildOnlyComponent_3>(linkedEntities[i].Value).value,
                    state.EntityManager.GetComponentData<ChildOnlyComponent_3>(linkedEntities2[i].Value).value);
                var b1 = state.EntityManager.GetBuffer<EnableableBuffer>(linkedEntities[i].Value);
                var b2 = state.EntityManager.GetBuffer<EnableableBuffer>(linkedEntities2[i].Value);
                Assert.AreEqual(b1.Length, b2.Length);
                for (int b = 0; b < b1.Length; b++)
                {
                    Assert.AreEqual(b1[b].value, b2[b].value);
                }
            }
            state.EntityManager.DestroyEntity(clientEntity2);
            state.Enabled = false;
        }
    }

    struct GhostSpawner : IComponentData
    {
        public Entity ghost;
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ServerSimulation)]
    partial class PredictSpawnGhost : SystemBase
    {
        public NetworkTick spawnTick;
        public PredictedGhostSpawnTests.PredictedGhostSpawnType spawnFromCommandBuffer;
        protected override void OnCreate()
        {
            RequireForUpdate<GhostSpawner>();
        }

        protected override void OnUpdate()
        {
            if(!spawnTick.IsValid)
                return;

            var spawner = SystemAPI.GetSingleton<GhostSpawner>();
            var serverTick = SystemAPI.GetSingleton<NetworkTime>();
            if (serverTick.IsFirstTimeFullyPredictingTick && !spawnTick.IsNewerThan(serverTick.ServerTick))
            {
                if (spawnFromCommandBuffer == PredictedGhostSpawnTests.PredictedGhostSpawnType.FromBeginFrame)
                {
                    var commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(World.Unmanaged);
                    var predictedEntity = commandBuffer.Instantiate(spawner.ghost);
                    commandBuffer.SetComponent(predictedEntity, new Data{Value = 100});
                }
                else if (spawnFromCommandBuffer == PredictedGhostSpawnTests.PredictedGhostSpawnType.FromEndPrediction)
                {
                    var commandBuffer = SystemAPI.GetSingleton<EndPredictedSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(World.Unmanaged);
                    var predictedEntity = commandBuffer.Instantiate(spawner.ghost);
                    commandBuffer.SetComponent(predictedEntity, new Data{Value = 100});
                }
                else
                {
                    var predictedEntity = EntityManager.Instantiate(spawner.ghost);
                    EntityManager.SetComponentData(predictedEntity, new Data{Value = 100});
                }
                spawnTick = default;
            }
        }
    }
    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateAfter(typeof(PredictSpawnGhost))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class PredictSpawnGhostUpdate : SystemBase
    {
        protected override void OnUpdate()
        {
            foreach(var data in SystemAPI.Query<RefRW<Data>>().WithAll<Simulate>())
            {
                ++data.ValueRW.Value;
            }
        }
    }

    class PredictedGhostSpawnTests
    {
        /* 创建预测 Ghost 和插值 Ghost 两个 Prefab
         *  - 验证客户端生成预测 Ghost 的行为符合预期
         *  - 验证服务器生成插值 Ghost 的行为符合预期
         *  - 验证客户端 Prefab 配置了正确的组件
         *  - 验证本地生成的预测 Ghost 能正确同步给其他客户端
         *  - 使用默认生成分类系统
         */
        [Test]
        public void PredictSpawnGhost()
        {
            const int PREDICTED = 0;
            const int INTERPOLATED = 1;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(UpdateDataSystem));

                // 预测 Ghost
                var predictedGhostGO = new GameObject("PredictedGO");
                predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new DataConverter();
                var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;
                ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;
                ghostConfig.HasOwner = true;

                // 在预测 Ghost 下嵌套一个子对象
                var predictedGhostGOChild = new GameObject("PredictedGO-Child");
                predictedGhostGOChild.AddComponent<TestNetCodeAuthoring>().Converter = new ChildDataConverter();
                predictedGhostGOChild.transform.parent = predictedGhostGO.transform;

                // 插值 Ghost
                var interpolatedGhostGO = new GameObject("InterpolatedGO");
                interpolatedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new DataConverter();
                ghostConfig = interpolatedGhostGO.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
                ghostConfig.SupportedGhostModes = GhostModeMask.Interpolated;

                Assert.IsTrue(testWorld.CreateGhostCollection(predictedGhostGO, interpolatedGhostGO));

                testWorld.CreateWorlds(true, 1);

                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // 在客户端预测生成 Ghost
                var prefabsListQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
                var prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
                var prefabs = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);
                var predictedPrefab = prefabs[PREDICTED].Value;
                var clientEntity = testWorld.ClientWorlds[0].EntityManager.Instantiate(predictedPrefab);

                // 验证实例化的是支持预测生成的 Prefab
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(clientEntity));
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(clientEntity));

                // 验证预测 Ghost 的 LinkedEntityGroup 包含子对象实体
                var linkedEntities = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                Assert.AreEqual(2, linkedEntities.Length);

                // 服务器为客户端预测生成的实体生成对应权威 Ghost
                prefabsListQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
                prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
                prefabs = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);
                Assert.IsFalse(testWorld.ServerWorld.EntityManager.HasComponent<PredictedGhostSpawnRequest>(prefabs[PREDICTED].Value));
                testWorld.ServerWorld.EntityManager.Instantiate(prefabs[PREDICTED].Value);


                for (int i = 0; i < 5; ++i)
                    testWorld.Tick();

                // 预测生成请求已经消费
                Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(clientEntity));

                // 验证客户端实例的 GhostField 已更新且只生成一个实体
                var compQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Data>());
                var clientData = compQuery.ToComponentDataArray<Data>(Allocator.Temp);
                Assert.AreEqual(1, clientData.Length);
                Assert.IsTrue(clientData[0].Value > 1);

                // 服务器生成普通插值 Ghost
                prefabsListQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
                prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
                prefabs = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);
                testWorld.ServerWorld.EntityManager.Instantiate(prefabs[INTERPOLATED].Value);
                Assert.IsFalse(testWorld.ServerWorld.EntityManager.HasComponent<PredictedGhostSpawnRequest>(prefabs[INTERPOLATED].Value));

                for (int i = 0; i < 5; ++i)
                    testWorld.Tick();

                // 验证客户端预测生成实例的 GhostField 已更新
                compQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new ComponentType[] { typeof(Data), typeof(PredictedGhost) },
                });
                compQuery.ToComponentDataArray<Data>(Allocator.Temp);
                Assert.AreEqual(1, clientData.Length);
                Assert.IsTrue(clientData[0].Value > 1);

                // 验证插值 Ghost 也已同步到客户端并完成更新
                compQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new ComponentType[] { typeof(Data) },
                    None = new ComponentType[] { typeof(PredictedGhost) }
                });
                compQuery.ToComponentDataArray<Data>(Allocator.Temp);
                Assert.AreEqual(1, clientData.Length);
                Assert.IsTrue(clientData[0].Value > 1);

                // 客户端使用同一个预测 Prefab 同时支持预测生成和普通服务器生成
                var queryDesc = new EntityQueryDesc
                {
                    All = new ComponentType[]
                    {
                        typeof(Data),
                        typeof(Prefab),
                        typeof(PredictedGhost)
                    },
                    Options = EntityQueryOptions.IncludePrefab
                };
                compQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(queryDesc);
                Assert.AreEqual(1, compQuery.CalculateEntityCount());

                // 验证 Prefab 副本中的子实体复制正确
                // 遍历每个预测 Prefab 的 LinkedEntityGroup
                // 检查其中的子实体是否正确反向引用父实体
                var entityPrefabs = compQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < entityPrefabs.Length; ++i)
                {
                    var parentEntity = entityPrefabs[i];
                    var links = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(parentEntity);
                    Assert.AreEqual(2, links.Length);
                    var child = links[1].Value;
                    var parentLink = testWorld.ClientWorlds[0].EntityManager.GetComponentData<Parent>(child).Value;
                    Assert.AreEqual(parentEntity, parentLink);
                }

                // 服务器应包含插值和预测两个 Prefab
                compQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(queryDesc);
                Assert.AreEqual(2, compQuery.CalculateEntityCount());
            }
        }

        [Test]
        public void CustomSpawnClassificationSystem()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(TestSpawnClassificationSystem));

                // 预测 Ghost
                var predictedGhostGO = new GameObject("PredictedGO");
                predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new DataConverter();
                var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;
                ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;
                ghostConfig.HasOwner = true;

                Assert.IsTrue(testWorld.CreateGhostCollection(predictedGhostGO));

                testWorld.CreateWorlds(true, 1);

                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // 在客户端预测生成 Ghost
                var prefabsListQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
                var prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
                var prefabs = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);
                var predictedPrefab = prefabs[0].Value;

                // 在同一帧实例化两个 Ghost
                testWorld.ClientWorlds[0].EntityManager.Instantiate(predictedPrefab);
                testWorld.ClientWorlds[0].EntityManager.Instantiate(predictedPrefab);

                // 服务器为客户端预测生成的实体生成对应权威 Ghost
                prefabsListQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
                prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
                prefabs = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);

                // 服务器同样实例化两个 Ghost
                testWorld.ServerWorld.EntityManager.Instantiate(prefabs[0].Value);
                testWorld.ServerWorld.EntityManager.Instantiate(prefabs[0].Value);

                for (int i = 0; i < 5; ++i)
                    testWorld.Tick();

                // 验证只有第一个 Spawn 由自定义分类系统处理，其余项由默认系统处理
                var classifiedGhosts = testWorld.ClientWorlds[0].GetExistingSystemManaged<TestSpawnClassificationSystem>();
                Assert.AreEqual(1, classifiedGhosts.PredictedEntities.Length);

                // 验证最终生成的 Ghost 总数正确
                var compQuery = testWorld.ClientWorlds[0].EntityManager
                    .CreateEntityQuery(typeof(Data));
                Assert.AreEqual(2, compQuery.CalculateEntityCount());
            }
        }

        internal enum PredictedSpawnDespawnDelay
        {
            DespawnAfterInterpolationTick,
            Despawn15AdditionalTicksLater,
        }
        [Test]
        public void IncorrectlyPredictedSpawnGhostsAreDestroyedCorrectly([Values]PredictedSpawnDespawnDelay predictedSpawnDespawnDelay)
        {
            var additionalDespawnDelayTicks = predictedSpawnDespawnDelay switch
            {
                PredictedSpawnDespawnDelay.DespawnAfterInterpolationTick => 0u,
                PredictedSpawnDespawnDelay.Despawn15AdditionalTicksLater => 15u,
                _ => throw new System.ArgumentOutOfRangeException(nameof(predictedSpawnDespawnDelay), predictedSpawnDespawnDelay, nameof(IncorrectlyPredictedSpawnGhostsAreDestroyedCorrectly)),
            };
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(VerifyInitialization));

            // 预测 Ghost
            var predictedGhostGO = new GameObject("BadPredictedGO");
            predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedGhostDataConverter();
            var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Predicted;
            ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;
            Assert.IsTrue(testWorld.CreateGhostCollection(predictedGhostGO));

            // 创建 World 并开始测试
            testWorld.CreateWorlds(true, 1);
            var clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            clientTickRate.NumAdditionalClientPredictedGhostLifetimeTicks = (ushort) additionalDespawnDelayTicks;
            var clientServerTickRate = new ClientServerTickRate();
            clientServerTickRate.ResolveDefaults();
            var interpolationBufferTimeInTicks = clientTickRate.CalculateInterpolationBufferTimeInTicks(in clientServerTickRate);
            testWorld.ClientWorlds[0].EntityManager.CreateSingleton(clientTickRate);
            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            // 在客户端预测生成 Ghost
            var expectedDespawnTick = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[0]).ServerTick;
            expectedDespawnTick.Add(additionalDespawnDelayTicks);
            var prefabsListQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
            var prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
            var prefabs = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);
            var predictedPrefab = prefabs[0].Value;
            var clientEntity = testWorld.ClientWorlds[0].EntityManager.Instantiate(predictedPrefab);


            // 等待 Interpolation Tick 追上预期销毁 Tick
            var existedForTicks = 0;
            var entityExists = false;
            var previouslyExisted = true;
            NetworkTick currentInterpolationTick = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[0]).InterpolationTick;
            int numTicksToWait = expectedDespawnTick.TicksSince(currentInterpolationTick) + 6; // 额外等待六个 Tick 作为误差余量
            for (int i = 0; i < numTicksToWait; i++)
            {
                // 验证预测生成实体从一开始就存在且销毁后不会再次出现
                entityExists = testWorld.ClientWorlds[0].EntityManager.Exists(clientEntity);
                if(i == 0) Assert.IsTrue(entityExists, $"Sanity: Client predicted spawn should be created from the outset!");
                if (entityExists) existedForTicks++;
                Assert.IsFalse(!previouslyExisted && entityExists, $"Client predicted spawn should be created from the outset, then destroyed, then NEVER created again!? entityExists:{entityExists}, previouslyExisted:{previouslyExisted} ");
                previouslyExisted = entityExists;
                testWorld.Tick();
            }

            // 验证销毁结果和实体存活时长
            Assert.IsFalse(entityExists, $"After {numTicksToWait} ticks, the client predicted spawn should have despawned, as despawn tick (of {expectedDespawnTick.ToFixedString()}) is != currentInterpolationTick:{currentInterpolationTick.ToFixedString()})!");
            Assert.IsTrue(existedForTicks >= interpolationBufferTimeInTicks + additionalDespawnDelayTicks, $"The client predicted spawn should have existed for at least interpolationBufferTimeInTicks:{interpolationBufferTimeInTicks} + NumAdditionalClientPredictedGhostLifetimeTicks:{additionalDespawnDelayTicks} ticks, but it only existed for {existedForTicks} ticks!");
        }

        internal enum PredictedGhostSpawnType
        {
            FromBeginFrame,
            FromEndPrediction,
            InsidePredictionLoop
        }

        [Test(Description = "This test verify predicted spawning initialize the entity data correctly in the snapshot buffer.")]
        public void PredictedSpawnGhostAreInitializedCorrectly([Values]bool enableComponents)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyInitialization));

                // 预测 Ghost
                var predictedGhostGO = new GameObject("PredictedGO");
                predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedGhostDataConverter();
                var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;
                ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;

                // 在预测 Ghost 下嵌套一个子对象
                var predictedGhostGOChild = new GameObject("PredictedGO-Child");
                predictedGhostGOChild.AddComponent<TestNetCodeAuthoring>().Converter = new ChildDataConverter();
                predictedGhostGOChild.transform.parent = predictedGhostGO.transform;

                Assert.IsTrue(testWorld.CreateGhostCollection(predictedGhostGO));

                testWorld.CreateWorlds(true, 1);

                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // 在客户端预测生成 Ghost
                var prefabsListQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
                var prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
                var prefabs = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);
                var predictedPrefab = prefabs[0].Value;
                var clientEntity = testWorld.ClientWorlds[0].EntityManager.Instantiate(predictedPrefab);

                InitializePredictedEntity(clientEntity, testWorld.ClientWorlds[0].EntityManager, enableComponents);

                // 验证实例化的是支持预测生成的 Prefab
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(clientEntity));
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(clientEntity));

                // 验证预测 Ghost 的 LinkedEntityGroup 包含子对象实体
                var linkedEntities = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                Assert.AreEqual(2, linkedEntities.Length);

                // 执行一个部分 Tick，验证客户端完成预测 Ghost 初始化并触发一次回滚
                testWorld.Tick(1f/180f);
                {
                    // PredictedGhostSpawnRequest 仍存在并将在下一 Tick 移除
                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<PredictedGhostSpawnRequest>(clientEntity));
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableComponent_0>(clientEntity), enableComponents);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableComponent_1>(clientEntity), true);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableComponent_2>(clientEntity), enableComponents);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableBuffer_0>(clientEntity), enableComponents);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableBuffer_1>(clientEntity), true);
                    linkedEntities = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableBuffer>(linkedEntities[1].Value),enableComponents);
                }
                ref var systemState = ref testWorld.ClientWorlds[0].Unmanaged.GetExistingSystemState<VerifyInitialization>();
                systemState.Enabled = true;
                testWorld.Tick(1f/180f);
                {
                    Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(clientEntity));
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableComponent_0>(clientEntity), enableComponents);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableComponent_1>(clientEntity), true);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableComponent_2>(clientEntity), enableComponents);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableBuffer_0>(clientEntity), enableComponents);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableBuffer_1>(clientEntity), true);
                    linkedEntities = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                    Assert.AreEqual(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<EnableableBuffer>(linkedEntities[1].Value),enableComponents);
                }
            }
        }

        private void InitializePredictedEntity(Entity clientEntity, EntityManager entityManager, bool enableComponents)
        {
            entityManager.SetComponentData(clientEntity, new GhostOwner{NetworkId = 1});
            entityManager.SetComponentData(clientEntity, new Data{Value = 10});
            entityManager.SetComponentData(clientEntity, new EnableableComponent_0(){ value= 100});
            entityManager.SetComponentData(clientEntity, new EnableableComponent_1(){ value= 200});
            entityManager.SetComponentData(clientEntity, new EnableableComponent_2(){ value= 300});
            var buffer = entityManager.GetBuffer<EnableableBuffer_0>(clientEntity);
            buffer.Add(new EnableableBuffer_0{value = 10});
            buffer.Add(new EnableableBuffer_0{value = 20});
            buffer.Add(new EnableableBuffer_0{value = 30});
            var buffer1 = entityManager.GetBuffer<EnableableBuffer_1>(clientEntity);
            buffer1.Add(new EnableableBuffer_1{value = 40});
            buffer1.Add(new EnableableBuffer_1{value = 50});
            buffer1.Add(new EnableableBuffer_1{value = 60});

            var childEntity = entityManager.GetBuffer<LinkedEntityGroup>(clientEntity)[1].Value;
            entityManager.SetComponentData(childEntity, new ChildData{Value = 10});
            entityManager.SetComponentData(childEntity, new ChildOnlyComponent_3{value = 20});
            var childBuffer = entityManager.GetBuffer<EnableableBuffer>(childEntity);
            childBuffer.Add(new EnableableBuffer{value = 10});
            childBuffer.Add(new EnableableBuffer{value = 20});
            childBuffer.Add(new EnableableBuffer{value = 30});

            // 保留部分组件的默认启用状态，使测试同时覆盖启用和禁用组件
            entityManager.SetComponentEnabled<EnableableComponent_0>(clientEntity, enableComponents);
            //entityManager.SetComponentEnabled<EnableableComponent_1>(clientEntity, enableComponents);
            entityManager.SetComponentEnabled<EnableableComponent_2>(clientEntity, enableComponents);
            entityManager.SetComponentEnabled<EnableableBuffer_0>(clientEntity, enableComponents);
            //entityManager.SetComponentEnabled<EnableableBuffer_1>(clientEntity, enableComponents);
            entityManager.SetComponentEnabled<EnableableBuffer>(childEntity, enableComponents);
        }

        internal enum PredictedSpawnRollbackOptions
        {
            RollbackToSpawnTick,
            DontRollbackToSpawnTick
        }
        internal enum KeepHistoryBufferOptions
        {
            UseHistoryBufferOnStructuralChanges,
            RollbackOnStructuralChanges
        }

        static void SetupSpawner(NetCodeTestWorld testWorld, World world, int prefabIndex)
        {
            var spawner = world.EntityManager.CreateEntity(typeof(GhostSpawner));
            world.EntityManager.SetComponentData(spawner, new GhostSpawner
            {
                ghost = testWorld.GetSingletonBuffer<NetCodeTestPrefab>(world)[prefabIndex].Value
            });
        }

        [Test(Description = "Test a current (little weird) condition that when spawning an entity from the command buffer, the spawn tick" + "is different for the client and server.")]
        public void PredictSpawnGhost_SpawnTick_DifferentForClientAndServer([Values]PredictedGhostSpawnType predictedGhostSpawnType)
        {
            var predictedGhostGO = new GameObject($"PredictedGO");
            predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new DataConverter();
            var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Predicted;
            ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;

            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(CountNumberOfRollbacksSystem),
                typeof(PredictSpawnGhost));

            Assert.IsTrue(testWorld.CreateGhostCollection(predictedGhostGO));
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();

            for (int i = 0; i < 32; ++i)
                testWorld.Tick();

            // 服务器先生成一个预测 Ghost，使预测循环能够持续执行回滚
            testWorld.SpawnOnServer(0);

            SetupSpawner(testWorld, testWorld.ServerWorld, 0);
            SetupSpawner(testWorld, testWorld.ClientWorlds[0], 0);

            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            var predictedGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance));
            Assert.AreEqual(1, predictedGhosts.CalculateEntityCount());

            // 确保客户端后续执行完整 Tick，让预测循环能够多次运行
            var clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            var spawnTick = clientTime.ServerTick;
            if(!clientTime.IsPartialTick)
                spawnTick.Add(1);

            testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().spawnFromCommandBuffer = predictedGhostSpawnType;
            testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().spawnTick = spawnTick;
            testWorld.ServerWorld.GetExistingSystemManaged<PredictSpawnGhost>().spawnFromCommandBuffer = predictedGhostSpawnType;
            testWorld.ServerWorld.GetExistingSystemManaged<PredictSpawnGhost>().spawnTick = spawnTick;

            // 在客户端生成 Ghost，通过 CommandBuffer 生成时还需一个 Tick 才会实际出现
            testWorld.Tick();
            var predictedSpawnRequests = new EntityQueryBuilder(Allocator.Temp).WithPresent<PredictedGhostSpawnRequest>().Build(testWorld.ClientWorlds[0].EntityManager);
            if(predictedGhostSpawnType == PredictedGhostSpawnType.FromBeginFrame)
                testWorld.Tick();
            Assert.AreEqual(1, predictedSpawnRequests.CalculateEntityCount());
            var spawnedGhost = predictedSpawnRequests.GetSingletonEntity();
            Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<PredictedGhostSpawnRequest>(spawnedGhost));
            Assert.AreEqual(spawnTick, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(spawnedGhost).spawnTick);
            // 继续推进并等待服务器生成对应 Ghost
            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            // 验证预测生成请求已完成分类，实体保留并采用服务器的 Spawn Tick
            Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(spawnedGhost));
            Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.Exists(spawnedGhost));
            var expectServerTick = spawnTick;
            if(predictedGhostSpawnType == PredictedGhostSpawnType.FromBeginFrame)
                expectServerTick.Increment();
            Assert.AreEqual(expectServerTick, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(spawnedGhost).spawnTick);
        }

        [Test(Description = "The test verify that predicted spawned ghost instantiated inside or outside the prediction loop" +
                            "correctly initialize their state and tick and respect both history and rollback settings")]
        public void PredictSpawnGhost_RollbackAndHistoryBackup(
            [Values]PredictedGhostSpawnType predictedGhostSpawnType,
            [Values]PredictedSpawnRollbackOptions rollback,
            [Values]KeepHistoryBufferOptions keepHistoryOnStructuralChanges)
        {
            var gameObjects = SetupGhosts(rollback, keepHistoryOnStructuralChanges);

            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(CountNumberOfRollbacksSystem),
                typeof(PredictSpawnGhost));

            Assert.IsTrue(testWorld.CreateGhostCollection(gameObjects));
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();

            for (int i = 0; i < 32; ++i)
                testWorld.Tick();

            // 服务器先生成一个预测 Ghost，使预测循环能够持续执行回滚
            testWorld.SpawnOnServer(0);

            SetupSpawner(testWorld, testWorld.ServerWorld, 0);
            SetupSpawner(testWorld, testWorld.ClientWorlds[0], 0);

            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            var predictedGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance));
            Assert.AreEqual(1, predictedGhosts.CalculateEntityCount());

            // 将客户端推进到完整 Tick，让预测循环能够多次运行
            var clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            testWorld.TickClientWorld((1 - clientTime.ServerTickFraction)/60f);
            clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            Assert.IsFalse(clientTime.IsPartialTick);
            var spawnTick = clientTime.ServerTick;
            spawnTick.Add(1);

            testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().spawnFromCommandBuffer = predictedGhostSpawnType;
            testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().spawnTick = spawnTick;
            testWorld.ServerWorld.GetExistingSystemManaged<PredictSpawnGhost>().spawnFromCommandBuffer = predictedGhostSpawnType;
            testWorld.ServerWorld.GetExistingSystemManaged<PredictSpawnGhost>().spawnTick = spawnTick;

            // 在客户端生成 Ghost，通过 CommandBuffer 生成时还需一个 Tick 才会实际出现
            testWorld.Tick();
            var predictedSpawnRequests = new EntityQueryBuilder(Allocator.Temp).WithPresent<PredictedGhostSpawnRequest>().Build(testWorld.ClientWorlds[0].EntityManager);
            if(predictedGhostSpawnType == PredictedGhostSpawnType.FromBeginFrame)
            {
                testWorld.Tick();
                Assert.AreEqual(1, predictedSpawnRequests.CalculateEntityCount());
            }
            var ghostWithRollback = predictedSpawnRequests.GetSingletonEntity();
            testWorld.ClientWorlds[0].EntityManager.SetName(ghostWithRollback, "PredictedSpawnedGhost");
            Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<PredictedGhostSpawnRequest>(ghostWithRollback));
            Assert.AreEqual(spawnTick, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(ghostWithRollback).spawnTick);
            // 执行较短的部分 Tick 以覆盖非完整 Tick 行为
            // 时间片必须足够小，避免客户端额外执行一个 Tick
            var partialTickFrac = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTickFraction;
            partialTickFrac /= 3f;
            testWorld.Tick((1f + partialTickFrac)/60f);
            Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(ghostWithRollback));
            var fromSpawnTickCount = testWorld.ClientWorlds[0].EntityManager.GetComponentData<CountSimulationFromSpawnTick>(ghostWithRollback);
            // 首次预测从 Spawn Tick 开始，因此计数增加一次
            Assert.AreEqual(1, fromSpawnTickCount.Value);
            // 在此制造结构变化，无法保留历史时会再次回滚到 Spawn Tick
            testWorld.ClientWorlds[0].EntityManager.RemoveComponent<EnableableComponent_0>(ghostWithRollback);
            testWorld.Tick(partialTickFrac/60f);
            fromSpawnTickCount = testWorld.ClientWorlds[0].EntityManager.GetComponentData<CountSimulationFromSpawnTick>(ghostWithRollback);
            // 两种配置都必须从 Spawn Tick 重新开始，一种使用该 Tick 的备份，另一种使用初始状态
            // 因此计数再增加一次并累计为二
            Assert.AreEqual(2, fromSpawnTickCount.Value);

            // 继续推进并等待服务器生成对应 Ghost
            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            // 验证分类结果正确，并考虑不同生成方式可能修正 Spawn Tick
            Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.Exists(ghostWithRollback));
            // 检查重新预测次数符合预期
            var expectedFromSpawnTickCount = fromSpawnTickCount.Value;
            var expectServerTick = spawnTick;
            if(predictedGhostSpawnType == PredictedGhostSpawnType.FromBeginFrame)
                expectServerTick.Increment();
            Assert.AreEqual(expectServerTick, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(ghostWithRollback).spawnTick);
            fromSpawnTickCount = testWorld.ClientWorlds[0].EntityManager.GetComponentData<CountSimulationFromSpawnTick>(ghostWithRollback);
            if (rollback == PredictedSpawnRollbackOptions.RollbackToSpawnTick)
                Assert.AreEqual(expectedFromSpawnTickCount, fromSpawnTickCount.Value);
            else
                Assert.AreEqual(expectedFromSpawnTickCount, fromSpawnTickCount.Value);
        }

        private static GameObject[] SetupGhosts(PredictedSpawnRollbackOptions rollback,
            KeepHistoryBufferOptions rollbackOnStructuralChanges)
        {
            var gameObjects = new GameObject[2];
            for (int i = 0; i < 2; ++i)
            {
                // 创建两个结构相同但回滚配置不同的预测 Ghost 类型
                var predictedGhostGO = new GameObject($"PredictedGO-{i}");
                predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedGhostDataConverter();
                predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new GhostWithRollbackConverter();
                var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;
                ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;
                if (i == 1)
                {
                    ghostConfig.RollbackPredictedSpawnedGhostState = rollback == PredictedSpawnRollbackOptions.RollbackToSpawnTick;
                    ghostConfig.RollbackPredictionOnStructuralChanges = rollbackOnStructuralChanges == KeepHistoryBufferOptions.RollbackOnStructuralChanges;
                }
                // 在预测 Ghost 下嵌套一个子对象
                var predictedGhostGOChild = new GameObject("PredictedGO-Child");
                predictedGhostGOChild.AddComponent<TestNetCodeAuthoring>().Converter = new ChildDataConverter();
                predictedGhostGOChild.transform.parent = predictedGhostGO.transform;
                gameObjects[i] = predictedGhostGO;
            }

            return gameObjects;
        }

        [Test(Description = "The test verify that predicted spawned ghost instantiated inside in the prediction loop" +
                            "don't mispredict and rewind correctly")]
        public void PredictSpawnGhost_InsidePrediction_AlwaysRollbackCorrectly([Values]PredictedSpawnRollbackOptions rollback,
            [Values]KeepHistoryBufferOptions keepHistoryBufferOnStructuralChanges)
        {
            var gameObjects = SetupGhosts(rollback, keepHistoryBufferOnStructuralChanges);
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(PredictSpawnGhost),
                typeof(PredictSpawnGhostUpdate), typeof(CountNumberOfRollbacksSystem));
            Assert.IsTrue(testWorld.CreateGhostCollection(gameObjects));
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();

            // 预测循环中必须先存在预测 Ghost，才能从循环内部生成另一个 Ghost
            testWorld.SpawnOnServer(0);

            SetupSpawner(testWorld, testWorld.ServerWorld, 1);
            SetupSpawner(testWorld, testWorld.ClientWorlds[0], 1);

            for (int i = 0; i < 32; ++i)
                testWorld.Tick();

            var predictedGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance));
            Assert.AreEqual(1, predictedGhosts.CalculateEntityCount());

            // 将客户端推进到已知的完整 Tick 状态
            var time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            testWorld.TickClientWorld((1 - time.ServerTickFraction)/60f);
            time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            Assert.IsFalse(time.IsPartialTick);
            var spawnTick = time.ServerTick;
            spawnTick.Add(1);

            // 只在客户端生成 Ghost 并验证以下行为
            // 部分 Tick 能正确回滚
            // Data 组件状态来自最近一个完整 Tick
            testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().spawnTick = spawnTick;
            testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictSpawnGhost>().spawnFromCommandBuffer = PredictedGhostSpawnType.InsidePredictionLoop;

            var predictedSpawnRequests = new EntityQueryBuilder(Allocator.Temp)
                .WithPresent<PredictedGhostSpawnRequest>().Build(testWorld.ClientWorlds[0].EntityManager);
            // 客户端将在完整 Tick 中生成实体，随后再执行一个部分 Tick
            // 生成时会为 Spawn Tick 建立新备份，但此时预测生成 Ghost 尚未完成初始化
            testWorld.Tick();
            var predictedSpawnEntity = predictedSpawnRequests.GetSingletonEntity();
            testWorld.TickClientWorld(.5f/60f);
            // Data 初始值为 100，每次预测更新都会递增，无论 Tick 是否完整
            // 请求消费后实体值应为 102，而快照缓冲中的备份值应为 101
            // 此处通过后续恢复结果间接验证备份值
            Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(predictedSpawnEntity));
            Assert.AreEqual(102, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
            var lastBackupTick = testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value;
            Assert.AreEqual(spawnTick.TickIndexForValidTick, lastBackupTick.TickIndexForValidTick);
            // 下一 Tick 会收到服务器为另一个 Ghost 发送的新状态
            // 此时已有备份和初始状态可供恢复，因此无论配置如何结果都应为 103
            testWorld.Tick();
            lastBackupTick = testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value;
            Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<PredictedGhostSpawnRequest>(predictedSpawnEntity));
            Assert.AreEqual(spawnTick.TickIndexForValidTick + 1, lastBackupTick.TickIndexForValidTick);
            Assert.AreEqual(103, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
            // 接着执行部分 Tick，根据配置可能从 Spawn Tick 27 重新预测到 29
            // 也可能从 Tick 28 的备份继续预测到 29，两种情况都应得到 103

            // 在此强制制造结构变化，验证系统仍能找到预期备份并从该处继续
            // 这会将实体移动到另一个 Chunk，避免复用原 Chunk，使测试结果更稳定
            testWorld.ClientWorlds[0].EntityManager.RemoveComponent<EnableableComponent_0>(predictedSpawnEntity);
            testWorld.TickClientWorld(0.25f/60f);
            lastBackupTick = testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value;
            Assert.AreEqual(spawnTick.TickIndexForValidTick + 1, lastBackupTick.TickIndexForValidTick);
            time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            Assert.IsTrue(time.IsPartialTick);
            // 结构变化后若不保留历史，需要从 Spawn Tick 执行两个预测 Tick
            var expectedPredictionCount = keepHistoryBufferOnStructuralChanges == KeepHistoryBufferOptions.RollbackOnStructuralChanges
                ? 2
                : 1;
            Assert.AreEqual(expectedPredictionCount, time.PredictedTickIndex);
            Assert.AreEqual(103, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
            // 重置计数器并接收服务器新数据，以验证系统是继续预测还是回滚
            testWorld.ClientWorlds[0].EntityManager.SetComponentData(predictedSpawnEntity, new CountSimulationFromSpawnTick{});
            testWorld.Tick();
            // 收到新的服务器 Ghost 更新后，预测次数至少应等于最近接收 Tick 与当前客户端 Tick 的差值
            time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            var lastReceivedTick = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]).LastReceivedSnapshotByLocal;
            var expectedPredictionTicks = time.ServerTick.TicksSince(lastReceivedTick);
            Assert.AreEqual(expectedPredictionTicks, time.PredictedTickIndex);
            // 无论从 101 回滚还是继续预测，最终值都应为 104
            Assert.AreEqual(104, testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(predictedSpawnEntity).Value);
            var expectedRewind = rollback == PredictedSpawnRollbackOptions.RollbackToSpawnTick ? 1 : 0;
            Assert.AreEqual(expectedRewind, testWorld.ClientWorlds[0].EntityManager.GetComponentData<CountSimulationFromSpawnTick>(predictedSpawnEntity).Value);
        }

        [Test(Description = "server side ghost has a ICleanupComponent that gets removed after all clients have acked the despawn. Testing there's no regression with the amount of time it takes to clean that up.")]
        public void GhostDespawn_SanityCheck()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var ghostGO = new GameObject("TestGhost");
            ghostGO.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedGhostDataConverter();
            var ghostConfig = ghostGO.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
            ghostConfig.SupportedGhostModes = GhostModeMask.Interpolated;

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGO));
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();

            // 在服务器生成 Ghost
            var serverEnt = testWorld.SpawnOnServer(0);

            // 推进 Tick 以便客户端生成 Ghost
            testWorld.TickMultiple(16);

            // 销毁 Ghost 并检查服务器清理实体所需时间
            testWorld.ServerWorld.EntityManager.DestroyEntity(serverEnt);

            for (int i = 0; i < 10; i++)
            {
                testWorld.Tick();
                var exists = testWorld.ServerWorld.EntityManager.Exists(serverEnt);
                if (i <= 2 && !exists)
                    Assert.Fail("GhostCleanup was removed too soon, most likely before the despawn was acked by the client");
                if (i > 2 && exists)
                    Assert.Fail("Tick count for server side ghost cleanup was exceeded, got a regression in the number of ticks it took to cleanup server ghosts. ");
            }
        }

        [Test]
        public void GhostDespawn_CheckLowNetworkRate()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var ghostGO = new GameObject("TestGhost");
            ghostGO.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedGhostDataConverter();
            var ghostConfig = ghostGO.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
            ghostConfig.SupportedGhostModes = GhostModeMask.Interpolated;

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGO));
            testWorld.CreateWorlds(true, 1);
            var tickRateEntity = testWorld.TryGetSingletonEntity<ClientServerTickRate>(testWorld.ServerWorld);
            if (tickRateEntity == Entity.Null)
                tickRateEntity = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(ClientServerTickRate));
            var tickRate = new ClientServerTickRate();
            tickRate.ResolveDefaults();
            tickRate.SimulationTickRate = 60;
            tickRate.NetworkTickRate = 15;
            testWorld.ServerWorld.EntityManager.SetComponentData<ClientServerTickRate>(tickRateEntity, tickRate);
            testWorld.Connect();
            testWorld.GoInGame();

            // 在服务器生成 Ghost
            var serverEnt = testWorld.SpawnOnServer(0);

            // 推进 Tick 以便客户端生成 Ghost
            testWorld.TickMultiple(16);

            // 销毁 Ghost 并检查低网络频率下服务器清理实体所需时间
            testWorld.ServerWorld.EntityManager.DestroyEntity(serverEnt);

            for (int i = 0; i < 50; i++)
            {
                testWorld.Tick();
                var exists = testWorld.ServerWorld.EntityManager.Exists(serverEnt);
                if (i <= 2 && !exists)
                    Assert.Fail("GhostCleanup was removed too soon, most likely before the despawn was acked by the client");
                if (i > 6 && exists)
                    Assert.Fail("Tick count for server side ghost cleanup was exceeded, got a regression in the number of ticks it took to cleanup server ghosts. ");
            }
        }

        [Test(Description = "Make sure that with lag and packet loss, the acking of despawns works properly.")]
        public void GhostDespawn_DespawnAck_WorksProperly()
        {
            // 过去曾在收到所有客户端确认前提前释放 Ghost ID，导致异常行为和断言失败
            // 此测试通过延迟和丢包复现该问题的触发条件
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.DriverSimulatedDelay = 200;
            testWorld.DriverSimulatedDrop = 15;

            var ghostGO = new GameObject("TestGhost");
            ghostGO.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedGhostDataConverter();
            var ghostConfig = ghostGO.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
            ghostConfig.SupportedGhostModes = GhostModeMask.Interpolated;

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGO));
            testWorld.CreateWorlds(true, 1);
            var tickRateEntity = testWorld.TryGetSingletonEntity<ClientServerTickRate>(testWorld.ServerWorld);
            if (tickRateEntity == Entity.Null)
                tickRateEntity = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(ClientServerTickRate));

            var tickRate = new ClientServerTickRate();
            tickRate.ResolveDefaults();
            tickRate.SimulationTickRate = 60;
            tickRate.NetworkTickRate = 15;
            testWorld.ServerWorld.EntityManager.SetComponentData<ClientServerTickRate>(tickRateEntity, tickRate);

            testWorld.Connect(maxSteps:100);
            testWorld.GoInGame();

            // 在服务器生成 Ghost
            var serverEnt = testWorld.SpawnOnServer(0);

            // 推进 Tick 以便客户端生成 Ghost
            testWorld.TickMultiple(16); // 保持该次数以对齐 Network Tick Rate 并复现潜在问题

            testWorld.ServerWorld.EntityManager.DestroyEntity(serverEnt);

            // 重复生成和销毁以覆盖随机丢包情况
            for (int i = 0; i < 100; i++)
            {
                testWorld.TickMultiple(2);
                var newEnt = testWorld.SpawnOnServer(0);
                testWorld.TickMultiple(2);
                testWorld.ServerWorld.EntityManager.DestroyEntity(newEnt);
            }
            testWorld.TickMultiple(500);

            // 确认服务器和客户端均已完成清理
            var existsServer = testWorld.ServerWorld.EntityManager.Exists(serverEnt);
            var existsClient = !testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance)).IsEmpty;
            Assert.IsFalse(existsClient);
            Assert.IsFalse(existsServer);
        }
    }
}
