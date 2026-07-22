using Unity.Burst;
using Unity.Entities;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.NetCode.LowLevel.Unsafe;
using System;
using Unity.Assertions;
using Unity.Burst.CompilerServices;
using Unity.Burst.Intrinsics;
using Unity.Jobs;

namespace Unity.NetCode
{
    /// <summary>
    /// 添加到包含 <see cref="PredictedGhostSpawn"/> Buffer 的 Singleton Entity 上的标签
    /// </summary>
    public struct PredictedGhostSpawnList : IComponentData
    {}

    /// <summary>
    /// 添加到 <see cref="PredictedGhostSpawnList"/> Singleton Entity
    /// 包含应当预生成的 Ghost 临时列表
    /// 该列表应在 <see cref="GhostSpawnClassificationSystem"/> 阶段处理
    /// InternalBufferCapacity 原本可以分配到接近占满 Chunk 内存
    /// 实际只需容纳客户端每帧主动创建的最大 Ghost Entity 数，通常为 0 到 1 个
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct PredictedGhostSpawn : IBufferElementData
    {
        /// <summary>
        /// 已生成的 Entity
        /// </summary>
        public Entity entity;
        /// <summary>
        /// Ghost 类型在 <see cref="GhostCollectionPrefab"/> 集合中的索引
        /// 供 <see cref="GhostSpawnClassificationSystem"/> 对 Ghost 分类
        /// </summary>
        public int ghostType;
        /// <summary>
        /// 生成该 Entity 时的服务器 Tick
        /// </summary>
        public NetworkTick spawnTick;

        /// <summary>
        /// 返回便于诊断的格式化信息
        /// </summary>
        /// <returns>格式化后的信息字符串</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString128Bytes ToFixedString() => $"PredictedGhostSpawn[ghostType:{ghostType},st:{spawnTick.ToFixedString()},ent:{entity.ToFixedString()}]";
        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }

    /// <summary>
    /// 需要在预测组内处理预测生成 Ghost Entity 的所有 System 的父组
    /// 该组在 <see cref="EndPredictedSimulationEntityCommandBufferSystem"/> 之后执行
    /// 以确保该 Command Buffer 创建的新预测 Ghost Entity 总能在当前预测 Tick 结束前完成初始化
    /// </summary>
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderLast = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateAfter(typeof(EndPredictedSimulationEntityCommandBufferSystem))]
    public partial class PredictedSpawningSystemGroup : ComponentSystemGroup
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            var spawnSystem = World.GetExistingSystem<PredictedGhostSpawnSystem>();
            AddSystemToUpdateList(spawnSystem);
        }
    }

    /// <summary>
    /// 通过初始化预测生成 Ghost 并将其加入 <see cref="PredictedGhostSpawn"/> Buffer
    /// 来消费所有 <see cref="PredictedGhostSpawnRequest"/> 请求
    /// 所有预测生成 Ghost 都会使用无效 GhostId 初始化，同时具有有效的 Ghost 类型和 spawnTick
    /// </summary>
    [UpdateInGroup(typeof(GhostSpawnSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [BurstCompile]
    public partial struct PredictedGhostSpawnSystem : ISystem
    {
        [BurstCompile]
        struct InitJob : IJobChunk
        {
            public DynamicTypeList DynamicTypeList;

            public Entity GhostCollectionSingleton;
            public EntityCommandBuffer commandBuffer;
            [ReadOnly] public BufferLookup<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefab> GhostCollectionFromEntity;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
            [ReadOnly] public EntityStorageInfoLookup childEntityLookup;
            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentLookup<GhostType> ghostTypeFromEntity;
            public NativeHashMap<GhostType, int>.ReadOnly GhostTypeToColletionIndex;
            public ComponentTypeHandle<PredictedGhostSpawnRequest> predictedSpawnTypeHandle;

            public ComponentTypeHandle<SnapshotData> snapshotDataType;
            public BufferTypeHandle<SnapshotDataBuffer> snapshotDataBufferType;
            public BufferTypeHandle<SnapshotDynamicDataBuffer> snapshotDynamicDataBufferType;

            public BufferLookup<PredictedGhostSpawn> spawnListFromEntity;
            public Entity spawnListEntity;

            public ComponentLookup<GhostInstance> ghostFromEntity;
            public ComponentLookup<PredictedGhost> predictedGhostFromEntity;

            public NetworkTick spawnTick;
            public NetDebug netDebug;

            public unsafe void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // 此 Job 不支持包含可启用 Component 类型的查询
                Assert.IsFalse(useEnabledMask);

                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr = DynamicTypeList.GetData();
                var entityList = chunk.GetNativeArray(entityType);
                var snapshotDataList = chunk.GetNativeArray(ref snapshotDataType);
                var snapshotDataBufferList = chunk.GetBufferAccessor(ref snapshotDataBufferType);
                var snapshotDynamicDataBufferList = chunk.GetBufferAccessor(ref snapshotDynamicDataBufferType);

                var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                var ghostType = ghostTypeFromEntity[entityList[0]];
                if (!GhostTypeToColletionIndex.TryGetValue(ghostType, out var ghostTypeIndex))
                {
                    // 当前还没有该 Ghost 的映射；此警告可能较频繁，但至少能确保问题被发现
                    // TODO: 可以考虑限制为最多输出 3 到 4 次
                    netDebug.LogError($"Failed to initialize predicted spawned ghost with type {(Hash128)ghostType}.\nThe ghost has been spawed before the client received from the server the required mapping (`GhostType -> index`),\nand the associated prefab loaded and processed by the GhostCollectionSystem.\nTo prevent this error/warning, you can check before spawning predicted ghosts that the GhostCollection.GhostTypeToColletionIndex hashmap contains a entry or the `GhostType` component assigned on the prefab.");
                    // 在此提前退出不会把该 Ghost 加入生成列表
                    // 如果客户端从服务器未加载的 Prefab 生成 Ghost，该 Entity 将不会被销毁
                    // 并且会持续报告此错误或警告；这是既有行为，此处没有改变语义
                    return;
                }
                // 当该 Ghost 对应 Prefab 已加载，但服务器列表中排在它之前的 Prefab 仍缺失时，此条件可能成立
                // GhostCollectionPrefabSerializer 集合会按顺序填充，因此当前索引仍可能超出已处理范围
                if(ghostTypeIndex >= GhostTypeCollection.Length)
                    return;
                var spawnList = spawnListFromEntity[spawnListEntity];
                var typeData = GhostTypeCollection[ghostTypeIndex];
                var snapshotSize = typeData.SnapshotSize;
                int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                int enableableMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.EnableableBits);
                int snapshotBaseOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) + changeMaskUints*sizeof(uint) + enableableMaskUints*sizeof(uint));

                var helper = new GhostSerializeHelper
                {
                    serializerState = new GhostSerializerState { GhostFromEntity = ghostFromEntity },
                    ghostChunkComponentTypesPtr = ghostChunkComponentTypesPtr,
                    GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton],
                    GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton],
                    childEntityLookup = childEntityLookup,
                    linkedEntityGroupType = linkedEntityGroupType,
                    ghostChunkComponentTypesPtrLen = DynamicTypeList.Length,
                    changeMaskUints = changeMaskUints
                };

                var bufferSizes = new NativeArray<int>(chunk.Count, Allocator.Temp);
                var hasBuffers = GhostTypeCollection[ghostTypeIndex].NumBuffers > 0;
                if (hasBuffers)
                    helper.GatherBufferSize(chunk, 0, chunk.Count, typeData, ref bufferSizes);

                for (int i = 0; i < entityList.Length; ++i)
                {
                    var entity = entityList[i];

                    var ghostComponent = ghostFromEntity[entity];
                    // 为预测生成 Ghost 设置有效 spawnTick 和无效 GhostId
                    // 这样可以将其与完全无效的 Ghost 实例区分开
                    ghostComponent.ghostId = 0;
                    ghostComponent.ghostType = ghostTypeIndex;
                    ghostComponent.spawnTick = spawnTick;
                    ghostFromEntity[entity] = ghostComponent;
                    predictedGhostFromEntity[entity] = new PredictedGhost{AppliedTick = spawnTick, PredictionStartTick = spawnTick};
                    // 设置初始 Snapshot 数据
                    // 获取各个 Buffer，并填入 Snapshot 大小等信息
                    snapshotDataList[i] = new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0};
                    var snapshotDataBuffer = snapshotDataBufferList[i];
                    snapshotDataBuffer.ResizeUninitialized(snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    var snapshotPtr = (byte*)snapshotDataBuffer.GetUnsafePtr();
                    UnsafeUtility.MemClear(snapshotPtr, snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    *(uint*)snapshotPtr = spawnTick.SerializedData;

                    helper.snapshotOffset = snapshotBaseOffset;
                    helper.snapshotPtr = snapshotPtr;
                    helper.snapshotSize = snapshotSize;
                    if (hasBuffers)
                    {
                        var dynamicDataCapacity= SnapshotDynamicBuffersHelper.CalculateBufferCapacity((uint)bufferSizes[i],
                            out var dynamicSnapshotSize);
                        var snapshotDynamicDataBuffer = snapshotDynamicDataBufferList[i];
                        var headerSize = SnapshotDynamicBuffersHelper.GetHeaderSize();
                        snapshotDynamicDataBuffer.ResizeUninitialized((int)dynamicDataCapacity);

                        // 客户端的动态 Buffer 数据偏移相对于动态数据槽位起点，而不是 Header
                        // 因此 dynamicSnapshotDataOffset 始终从 0 开始，而首个槽位的数据紧跟在 Header 之后
                        helper.snapshotDynamicPtr = (byte*)snapshotDynamicDataBuffer.GetUnsafePtr() + headerSize;
                        helper.snapshotDynamicHeaderPtr = (byte*)snapshotDynamicDataBuffer.GetUnsafePtr();
                        helper.dynamicSnapshotDataOffset = 0;
                        helper.dynamicSnapshotCapacity = (int)(dynamicSnapshotSize);
                    }
                    helper.CopyEntityToSnapshot(chunk, i, typeData, GhostSerializeHelper.ClearOption.DontClear);
                    // 移除请求 Component
                    // 加入预测生成列表，后续可考虑使用 Singleton 让其他生成 System 直接访问
                    spawnList.Add(new PredictedGhostSpawn{entity = entity, ghostType = ghostTypeIndex, spawnTick = spawnTick});
                    commandBuffer.RemoveComponent<PredictedGhostSpawnRequest>(entity);
                }
                chunk.SetComponentEnabledForAll(ref predictedSpawnTypeHandle, true);
                bufferSizes.Dispose();
            }
        }

        EntityQuery m_GhostInitQuery;
        NetworkTick m_LastFrameFullTick;

        BufferLookup<PredictedGhostSpawn> m_PredictedGhostSpawnFromEntity;
        BufferLookup<GhostComponentSerializer.State> m_GhostComponentSerializerStateFromEntity;
        BufferLookup<GhostCollectionPrefabSerializer> m_GhostCollectionPrefabSerializerFromEntity;
        BufferLookup<GhostCollectionComponentIndex> m_GhostCollectionComponentIndexFromEntity;
        BufferLookup<GhostCollectionPrefab> m_GhostCollectionPrefabFromEntity;
        EntityTypeHandle m_EntityTypeHandle;
        ComponentTypeHandle<SnapshotData> m_SnapshotDataHandle;
        BufferTypeHandle<SnapshotDataBuffer> m_SnapshotDataBufferHandle;
        BufferTypeHandle<SnapshotDynamicDataBuffer> m_SnapshotDynamicDataBufferHandle;
        BufferTypeHandle<LinkedEntityGroup> m_LinkedEntityGroupHandle;
        ComponentLookup<GhostInstance> m_GhostComponentFromEntity;
        ComponentLookup<PredictedGhost> m_PredictedGhostFromEntity;
        ComponentLookup<GhostType> m_GhostTypeComponentFromEntity;
        ComponentTypeHandle<PredictedGhostSpawnRequest> m_PredictedSpawnTypeHandle;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            var ent = state.EntityManager.CreateEntity();
            state.EntityManager.SetName(ent, (FixedString64Bytes)"PredictedGhostSpawnList");
            state.EntityManager.AddComponentData(ent, new PredictedGhostSpawnList{});
            state.EntityManager.AddBuffer<PredictedGhostSpawn>(ent);
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GhostType>()
                .WithDisabled<PredictedGhostSpawnRequest>()
                .WithAllRW<GhostInstance>();
            m_GhostInitQuery = state.GetEntityQuery(builder);
            m_PredictedGhostSpawnFromEntity = state.GetBufferLookup<PredictedGhostSpawn>();

            m_GhostComponentSerializerStateFromEntity = state.GetBufferLookup<GhostComponentSerializer.State>(true);
            m_GhostCollectionPrefabSerializerFromEntity = state.GetBufferLookup<GhostCollectionPrefabSerializer>(true);
            m_GhostCollectionComponentIndexFromEntity = state.GetBufferLookup<GhostCollectionComponentIndex>(true);
            m_GhostCollectionPrefabFromEntity = state.GetBufferLookup<GhostCollectionPrefab>(true);

            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_SnapshotDataHandle = state.GetComponentTypeHandle<SnapshotData>();
            m_SnapshotDataBufferHandle = state.GetBufferTypeHandle<SnapshotDataBuffer>();
            m_SnapshotDynamicDataBufferHandle = state.GetBufferTypeHandle<SnapshotDynamicDataBuffer>();
            m_LinkedEntityGroupHandle = state.GetBufferTypeHandle<LinkedEntityGroup>(true);
            m_PredictedSpawnTypeHandle = state.GetComponentTypeHandle<PredictedGhostSpawnRequest>();

            m_GhostComponentFromEntity = state.GetComponentLookup<GhostInstance>();
            m_PredictedGhostFromEntity = state.GetComponentLookup<PredictedGhost>();
            m_GhostTypeComponentFromEntity = state.GetComponentLookup<GhostType>(true);

            state.RequireForUpdate<PredictedGhostSpawnList>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            if (networkTime.IsInPredictionLoop && !networkTime.IsFirstTimeFullyPredictingTick)
                return;
            if (m_GhostInitQuery.IsEmpty)
            {
                m_LastFrameFullTick = NetworkTimeHelper.LastFullServerTick(SystemAPI.GetSingleton<NetworkTime>());
                return;
            }
            // 边界情况：预测生成发生在最初的 Tick
            // 如果 Ghost 由 PredictedSimulation 内的 System 生成便不会发生
            // 但在预测循环之外且收到首个 Snapshot 前生成时可能出现
            if(!m_LastFrameFullTick.IsValid)
                m_LastFrameFullTick = NetworkTimeHelper.LastFullServerTick(networkTime);
            // 由于没有客户端实际生成 Ghost 的时间信息，这里需要进行推断
            // 能用于匹配最近一次或第一次 IsFirstTimeFullyPredictingTick 的值只有上一个完整 Tick
            // 但一次经过时间可能推进多个这样的 Tick
            // 某些预测场景中，Tick T 的 Command 可能在 T+1 或 T+2 才触发生成，例如受射速限制时
            // 此时这里分配的生成 Tick 会有误差，通常相差 1 到 2 个 Tick
            // 在游戏运行速率接近或高于 Simulation Tick Rate 的常见场景中，生成 Tick 通常可以正确分配
            NetworkTick spawnTick;
            if(networkTime.IsInPredictionLoop)
                spawnTick = networkTime.ServerTick;
            else
            {
                // 在预测循环之外生成 Entity 时，客户端与服务器始终会分配不同的 Tick
                // 服务器在帧末的 GhostSendSystem 中分配 Tick，而客户端在帧初的本 System 中分配 Tick
                // 按客户端当前掌握的信息，Entity 应生成于上一帧完成的最后一个完整 Tick
                // 受经过时间和 Tick Batching 配置等因素影响，两端的生成 Tick 仍会不同，通常相差 1 个 Tick
                // 因此默认的 Tick 匹配会使用至少 [-5,+5] 的范围，为时序不一致预留足够空间
                // 这里不能简单增加 spawnTick 来强行匹配，因为该 Tick 表示 Entity 当前状态所在的 Tick
                // 不一定是 Entity 实际生成的时刻；该 Tick 还会写入 Snapshot，并在继续预测时作为回退和重新模拟的起点
                // 因此必须保持其状态语义一致
                spawnTick = m_LastFrameFullTick;
            }
            m_LastFrameFullTick = NetworkTimeHelper.LastFullServerTick(networkTime);

            var spawnListEntity = SystemAPI.GetSingletonEntity<PredictedGhostSpawnList>();
            m_PredictedGhostSpawnFromEntity.Update(ref state);
            m_GhostComponentSerializerStateFromEntity.Update(ref state);
            m_GhostCollectionPrefabSerializerFromEntity.Update(ref state);
            m_GhostCollectionComponentIndexFromEntity.Update(ref state);
            m_GhostCollectionPrefabFromEntity.Update(ref state);

            m_EntityTypeHandle.Update(ref state);
            m_SnapshotDataHandle.Update(ref state);
            m_SnapshotDataBufferHandle.Update(ref state);
            m_SnapshotDynamicDataBufferHandle.Update(ref state);
            m_LinkedEntityGroupHandle.Update(ref state);

            m_GhostComponentFromEntity.Update(ref state);
            m_PredictedGhostFromEntity.Update(ref state);
            m_GhostTypeComponentFromEntity.Update(ref state);
            m_PredictedSpawnTypeHandle.Update(ref state);
            var ghostCollection = SystemAPI.GetSingletonEntity<GhostCollection>();
            EntityCommandBuffer commandBuffer;
            commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            var initJob = new InitJob
            {
                GhostCollectionSingleton = ghostCollection,
                GhostComponentCollectionFromEntity = m_GhostComponentSerializerStateFromEntity,
                GhostTypeCollectionFromEntity = m_GhostCollectionPrefabSerializerFromEntity,
                GhostComponentIndexFromEntity = m_GhostCollectionComponentIndexFromEntity,
                GhostCollectionFromEntity = m_GhostCollectionPrefabFromEntity,
                GhostTypeToColletionIndex = state.EntityManager.GetComponentData<GhostCollection>(ghostCollection).GhostTypeToColletionIndex,
                commandBuffer = commandBuffer,
                entityType = m_EntityTypeHandle,
                snapshotDataType = m_SnapshotDataHandle,
                snapshotDataBufferType = m_SnapshotDataBufferHandle,
                snapshotDynamicDataBufferType = m_SnapshotDynamicDataBufferHandle,
                predictedSpawnTypeHandle = m_PredictedSpawnTypeHandle,
                ghostFromEntity = m_GhostComponentFromEntity,
                predictedGhostFromEntity = m_PredictedGhostFromEntity,
                ghostTypeFromEntity = m_GhostTypeComponentFromEntity,
                spawnTick = spawnTick,
                linkedEntityGroupType = m_LinkedEntityGroupHandle,
                childEntityLookup = state.GetEntityStorageInfoLookup(),
                spawnListFromEntity = m_PredictedGhostSpawnFromEntity,
                spawnListEntity = spawnListEntity,
                netDebug = SystemAPI.GetSingleton<NetDebug>()
            };
            var ghostComponentCollection = state.EntityManager.GetBuffer<GhostCollectionComponentType>(initJob.GhostCollectionSingleton);
            DynamicTypeList.PopulateList(ref state, ghostComponentCollection, true, ref initJob.DynamicTypeList);
            // 这里有意使用非并行的 ScheduleByRef
            state.Dependency = initJob.ScheduleByRef(m_GhostInitQuery, state.Dependency);
        }
    }

    /// <summary>
    /// 清理在有效期限内未与服务器 Ghost 完成分类匹配的预测生成 Ghost
    /// 匹配成功的 Ghost 会提前从 <see cref="PredictedGhostSpawn"/> Buffer 移除
    /// </summary>
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateAfter(typeof(GhostDespawnSystem))]
    [BurstCompile]
    public partial struct PredictedGhostDespawnSystem : ISystem
    {
        /// <summary>
        /// 销毁客户端上已经过期的预测生成 Entity
        /// 即未完成分类匹配、因而仍留在列表中的 Entity
        /// </summary>
        [BurstCompile]
        struct CleanupPredictedSpawns : IJob
        {
            public DynamicBuffer<PredictedGhostSpawn> spawnList;
            public NetworkTick destroyTick;
            public EntityCommandBuffer commandBuffer;
            public void Execute()
            {
                for (int i = 0; i < spawnList.Length; ++i)
                {
                    var ghost = spawnList[i];
                    if (Hint.Unlikely(destroyTick.IsNewerThan(ghost.spawnTick)))
                    {
                        // 销毁 Entity 并从列表移除
                        commandBuffer.DestroyEntity(ghost.entity);
                        spawnList.RemoveAtSwapBack(i);
                        --i;
                    }
                }
            }
        }

        BufferLookup<PredictedGhostSpawn> m_PredictedGhostSpawnLookup;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PredictedGhostSpawnList>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var spawnList = SystemAPI.GetSingletonBuffer<PredictedGhostSpawn>();
            if(spawnList.Length == 0)
                return;
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            if(!networkTime.InterpolationTick.IsValid)
                return;
            EntityCommandBuffer commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            var destroyTick = networkTime.InterpolationTick;
            // 应在完整插值 Tick 上执行 Despawn
            if(networkTime.InterpolationTickFraction < 1)
                destroyTick.Decrement();
            if(!SystemAPI.TryGetSingleton(out ClientTickRate clientTickRate))
                clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            destroyTick.Subtract(clientTickRate.NumAdditionalClientPredictedGhostLifetimeTicks);
            var cleanupJob = new CleanupPredictedSpawns
            {
                spawnList = spawnList,
                destroyTick = destroyTick,
                commandBuffer = commandBuffer,
            };
            state.Dependency = cleanupJob.Schedule(state.Dependency);
        }
    }
}
