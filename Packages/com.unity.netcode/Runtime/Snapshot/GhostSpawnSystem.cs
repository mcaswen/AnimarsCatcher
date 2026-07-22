using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>
    /// 负责为客户端 World 生成全部 Ghost 实体的系统
    /// </para>
    /// <para>
    /// 从服务器收到 Ghost Snapshot 时，<see cref="GhostReceiveSystem"/> 会向 <see cref="GhostSpawnBuffer"/> 添加生成请求
    /// 生成请求由 <see cref="GhostSpawnClassificationSystem"/> 分类后
    /// <see cref="GhostSpawnSystem"/> 开始处理生成队列
    /// </para>
    /// <para>
    /// 系统会根据生成类型 <see cref="GhostSpawnBuffer.Type"/> 以不同方式处理请求
    /// </para>
    /// <para>模式设为 <see cref="GhostSpawnBuffer.Type.Interpolated"/> 时
    /// Ghost 创建会延迟到 <see cref="NetworkTime.InterpolationTick"/> 等于或大于服务器实际生成 Tick
    /// 系统会创建带有 <see cref="PendingSpawnPlaceholder"/> 标签的临时实体
    /// 用于保存生成信息和从服务器收到的 Snapshot 数据
    /// 该实体会一直存在，直到真实 Ghost 实例生成或收到销毁请求
    /// 它只负责接收新的 Snapshot，这些数据不会应用到实体，因为它还不是真实 Ghost
    /// </para>
    /// <para>
    /// 模式设为 <see cref="GhostSpawnBuffer.Type.Predicted"/> 时
    /// 如果当前模拟 <see cref="NetworkTime.ServerTick"/> 大于或等于服务器报告的生成 Tick
    /// 就会立即生成新的 Ghost 实例
    /// 这通常是常态，因为客户端时间线，即当前模拟 Tick，应领先于服务器
    /// </para>
    /// <para>
    /// 否则 Ghost 创建会延迟到 <see cref="NetworkTime.ServerTick"/> 大于或等于所需生成 Tick
    /// 与插值 Ghost 类似，系统会创建临时占位实体保存生成信息及新收到的 Snapshot
    /// </para>
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(GhostSpawnSystemGroup))]
    public partial struct GhostSpawnSystem : ISystem
    {
        struct DelayedSpawnGhost
        {
            public int ghostId;
            public int ghostType;
            public NetworkTick clientSpawnTick;
            public NetworkTick serverSpawnTick;
            public Entity oldEntity;
            public Entity predictedSpawnEntity;
        }
        NativeQueue<DelayedSpawnGhost> m_DelayedInterpolatedGhostSpawnQueue;
        NativeQueue<DelayedSpawnGhost> m_DelayedPredictedGhostSpawnQueue;

        EntityQuery m_InGameGroup;
        EntityQuery m_NetworkIdQuery;
        EntityQuery m_InstanceCount;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            m_DelayedInterpolatedGhostSpawnQueue = new NativeQueue<DelayedSpawnGhost>(Allocator.Persistent);
            m_DelayedPredictedGhostSpawnQueue = new NativeQueue<DelayedSpawnGhost>(Allocator.Persistent);
            m_InGameGroup = state.GetEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>());
            m_NetworkIdQuery = state.GetEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.Exclude<NetworkStreamRequestDisconnect>());
            m_InstanceCount = state.GetEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadWrite<Simulate>(), ComponentType.Exclude<PendingSpawnPlaceholder>());

            var ent = state.EntityManager.CreateEntity();
            state.EntityManager.SetName(ent, "GhostSpawnQueue");
            state.EntityManager.AddComponentData(ent, default(GhostSpawnQueue));
            state.EntityManager.AddBuffer<GhostSpawnBuffer>(ent);
            state.EntityManager.AddBuffer<SnapshotDataBuffer>(ent);
            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<GhostSpawnQueue>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            state.CompleteDependency();
            m_DelayedPredictedGhostSpawnQueue.Dispose();
            m_DelayedInterpolatedGhostSpawnQueue.Dispose();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public unsafe void OnUpdate(ref SystemState state)
        {
            state.Dependency.Complete(); // 等待完成以访问 Ghost 映射
            if (state.WorldUnmanaged.IsThinClient())
                return;
            var stateEntityManager = state.EntityManager;
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var interpolationTargetTick = networkTime.InterpolationTick;
            if (networkTime.InterpolationTickFraction < 1 && interpolationTargetTick.IsValid)
                interpolationTargetTick.Decrement();
            var predictionTargetTick = networkTime.ServerTick;
            var prefabsEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
            var prefabs = stateEntityManager.GetBuffer<GhostCollectionPrefab>(prefabsEntity).ToNativeArray(Allocator.Temp);

            ref var ghostCount = ref SystemAPI.GetSingletonRW<GhostCount>().ValueRW;
            var ghostSpawnEntity = SystemAPI.GetSingletonEntity<GhostSpawnQueue>();
            var ghostSpawnBufferComponent = stateEntityManager.GetBuffer<GhostSpawnBuffer>(ghostSpawnEntity);
            var snapshotDataBufferComponent = stateEntityManager.GetBuffer<SnapshotDataBuffer>(ghostSpawnEntity);

            // Stream 尚未进入游戏时避免添加新 Ghost
            if (m_InGameGroup.IsEmptyIgnoreFilter)
            {
                ghostSpawnBufferComponent.ResizeUninitialized(0);
                snapshotDataBufferComponent.ResizeUninitialized(0);
                m_DelayedPredictedGhostSpawnQueue.Clear();
                m_DelayedInterpolatedGhostSpawnQueue.Clear();
                return;
            }

            var ghostSpawnBuffer = ghostSpawnBufferComponent.ToNativeArray(Allocator.Temp);
            var snapshotDataBuffer = snapshotDataBufferComponent.ToNativeArray(Allocator.Temp);
            ghostSpawnBufferComponent.ResizeUninitialized(0);
            snapshotDataBufferComponent.ResizeUninitialized(0);

            var spawnedGhosts = new NativeList<SpawnedGhostMapping>(16, Allocator.Temp);
            var nonSpawnedGhosts = new NativeList<NonSpawnedGhostMapping>(16, Allocator.Temp);
            var ghostCollectionSingleton = SystemAPI.GetSingletonEntity<GhostCollection>();
            for (int i = 0; i < ghostSpawnBuffer.Length; ++i)
            {
                var ghost = ghostSpawnBuffer[i];
                Entity entity = Entity.Null;
                byte* snapshotData = null;

                var ghostTypeCollection = stateEntityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollectionSingleton);
                var snapshotSize = ghostTypeCollection[ghost.GhostType].SnapshotSize;
                bool hasBuffers = ghostTypeCollection[ghost.GhostType].NumBuffers > 0;

                if (ghost.SpawnType == GhostSpawnBuffer.Type.Interpolated)
                {
                    entity = AddToDelayedSpawnQueue(ref stateEntityManager, m_DelayedInterpolatedGhostSpawnQueue, ghost, ref snapshotDataBuffer, ghostTypeCollection);

                    nonSpawnedGhosts.Add(new NonSpawnedGhostMapping { ghostId = ghost.GhostID, entity = entity });
                }
                else if (ghost.SpawnType == GhostSpawnBuffer.Type.Predicted)
                {
                    // 检查是否可以立即生成
                    if (!ghost.ClientSpawnTick.IsNewerThan(predictionTargetTick))
                    {
                        // TODO：报错前可以为 Prefab 加载预留一段时间
                        if (prefabs[ghost.GhostType].GhostPrefab == Entity.Null)
                        {
                            ReportMissingPrefab(ref stateEntityManager);
                            continue;
                        }
                        // 直接生成
                        entity = ghost.PredictedSpawnEntity != Entity.Null ? ghost.PredictedSpawnEntity : stateEntityManager.Instantiate(prefabs[ghost.GhostType].GhostPrefab);
                        if(stateEntityManager.HasComponent<PredictedGhostSpawnRequest>(entity))
                            stateEntityManager.RemoveComponent<PredictedGhostSpawnRequest>(entity);
                        if (stateEntityManager.HasComponent<GhostPrefabMetaData>(prefabs[ghost.GhostType].GhostPrefab))
                        {
                            ref var toRemove = ref stateEntityManager.GetComponentData<GhostPrefabMetaData>(prefabs[ghost.GhostType].GhostPrefab).Value.Value.DisableOnPredictedClient;
                            // 移除组件会产生结构变更并使 Buffer 指针失效，因此需要先复制 LinkedEntityGroup
                            var linkedEntityGroup = stateEntityManager.GetBuffer<LinkedEntityGroup>(entity).ToNativeArray(Allocator.Temp);
                            for (int rm = 0; rm < toRemove.Length; ++rm)
                            {
                                var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(toRemove[rm].StableHash));
                                stateEntityManager.RemoveComponent(linkedEntityGroup[toRemove[rm].EntityIndex].Value, compType);
                            }
                        }
                        stateEntityManager.SetComponentData(entity, new GhostInstance {ghostId = ghost.GhostID, ghostType = ghost.GhostType, spawnTick = ghost.ServerSpawnTick});
                        if (PrespawnHelper.IsPrespawnGhostId(ghost.GhostID))
                            ConfigurePrespawnGhost(ref stateEntityManager, entity, ghost);
                        var newBuffer = stateEntityManager.GetBuffer<SnapshotDataBuffer>(entity);
                        newBuffer.ResizeUninitialized(snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                        snapshotData = (byte*)newBuffer.GetUnsafePtr();
                        stateEntityManager.SetComponentData(entity, new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0});
                        spawnedGhosts.Add(new SpawnedGhostMapping{ghost = new SpawnedGhost{ghostId = ghost.GhostID, spawnTick = ghost.ServerSpawnTick}, entity = entity});

                        UnsafeUtility.MemClear(snapshotData, snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                        UnsafeUtility.MemCpy(snapshotData, (byte*)snapshotDataBuffer.GetUnsafeReadOnlyPtr() + ghost.DataOffset, snapshotSize);
                        if (hasBuffers)
                        {
                            // 调整大小并复制关联的 Dynamic Buffer Snapshot 数据
                            var snapshotDynamicBuffer = stateEntityManager.GetBuffer<SnapshotDynamicDataBuffer>(entity);
                            var dynamicDataCapacity= SnapshotDynamicBuffersHelper.CalculateBufferCapacity(ghost.DynamicDataSize, out var _);
                            snapshotDynamicBuffer.ResizeUninitialized((int)dynamicDataCapacity);
                            var dynamicSnapshotData = (byte*)snapshotDynamicBuffer.GetUnsafePtr();
                            if(dynamicSnapshotData == null)
                                throw new InvalidOperationException("snapshot dynamic data buffer not initialized but ghost has dynamic buffer contents");

                            // 将当前槽位的已用大小写入动态数据 Header，即 uint[GhostSystemConstants.SnapshotHistorySize]
                            // 对新生成实体而言当前槽位为 0
                            // 无需将所有 Header 槽位初始化为 0，因为这些信息只用于增量压缩
                            // 且增量压缩依赖已 Ack Tick，通常只会访问已初始化且相关的槽位
                            // 布局详情参见 SnapshotData.cs
                            ((uint*)dynamicSnapshotData)[0] = ghost.DynamicDataSize;
                            var headerSize = SnapshotDynamicBuffersHelper.GetHeaderSize();
                            UnsafeUtility.MemCpy(dynamicSnapshotData + headerSize, (byte*)snapshotDataBuffer.GetUnsafeReadOnlyPtr() + ghost.DataOffset + snapshotSize, ghost.DynamicDataSize);
                        }
                    }
                    else
                    {
                        // 加入延迟生成队列
                        entity = AddToDelayedSpawnQueue(ref stateEntityManager, m_DelayedPredictedGhostSpawnQueue, ghost, ref snapshotDataBuffer, ghostTypeCollection);

                        nonSpawnedGhosts.Add(new NonSpawnedGhostMapping { ghostId = ghost.GhostID, entity = entity });
                    }
                }
            }
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            ref var ghostEntityMap = ref SystemAPI.GetSingletonRW<SpawnedGhostEntityMap>().ValueRW;
            ghostEntityMap.AddClientNonSpawnedGhosts(nonSpawnedGhosts.AsArray(), netDebug);
            ghostEntityMap.AddClientSpawnedGhosts(spawnedGhosts.AsArray(), netDebug);

            spawnedGhosts.Clear();
            while (m_DelayedInterpolatedGhostSpawnQueue.Count > 0 &&
                   !m_DelayedInterpolatedGhostSpawnQueue.Peek().clientSpawnTick.IsNewerThan(interpolationTargetTick))
            {
                var ghost = m_DelayedInterpolatedGhostSpawnQueue.Dequeue();
                if (TrySpawnFromDelayedQueue(ref stateEntityManager, ghost, GhostSpawnBuffer.Type.Interpolated, prefabs, ghostCollectionSingleton, out var entity))
                {
                    spawnedGhosts.Add(new SpawnedGhostMapping { ghost = new SpawnedGhost { ghostId = ghost.ghostId, spawnTick = ghost.serverSpawnTick }, entity = entity, previousEntity = ghost.oldEntity });
                }
            }
            while (m_DelayedPredictedGhostSpawnQueue.Count > 0 &&
                   !m_DelayedPredictedGhostSpawnQueue.Peek().clientSpawnTick.IsNewerThan(predictionTargetTick))
            {
                var ghost = m_DelayedPredictedGhostSpawnQueue.Dequeue();
                if (TrySpawnFromDelayedQueue(ref stateEntityManager, ghost, GhostSpawnBuffer.Type.Predicted, prefabs, ghostCollectionSingleton, out var entity))
                {
                    spawnedGhosts.Add(new SpawnedGhostMapping { ghost = new SpawnedGhost { ghostId = ghost.ghostId, spawnTick = ghost.serverSpawnTick }, entity = entity, previousEntity = ghost.oldEntity });
                }
            }
            ghostEntityMap.UpdateClientSpawnedGhosts(spawnedGhosts.AsArray(), netDebug);

            ghostCount.m_GhostCompletionCount[2] = m_InstanceCount.CalculateEntityCountWithoutFiltering();
        }

        void ConfigurePrespawnGhost(ref EntityManager entityManager, Entity entity, in GhostSpawnBuffer ghost)
        {
            if(ghost.PrespawnIndex == -1)
                throw new InvalidOperationException("respawning a pre-spawned ghost requires a valid prespawn index");
            entityManager.AddComponentData(entity, new PreSpawnedGhostIndex {Value = ghost.PrespawnIndex});
            entityManager.AddSharedComponent(entity, new SceneSection
            {
                SceneGUID = ghost.SceneGUID,
                Section = ghost.SectionIndex
            });
        }

        void ReportMissingPrefab(ref EntityManager entityManager)
        {
            SystemAPI.GetSingleton<NetDebug>().LogError($"Trying to spawn with a prefab which is not loaded");

            // TODO：可用后改用 entityManager.AddComponentData(EntityQuery, T)
            using var entities = m_NetworkIdQuery.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                entityManager.AddComponentData(entity, new NetworkStreamRequestDisconnect {Reason = NetworkStreamDisconnectReason.BadProtocolVersion});
            }
        }

        unsafe Entity AddToDelayedSpawnQueue(ref EntityManager entityManager, NativeQueue<DelayedSpawnGhost> delayedSpawnQueue, in GhostSpawnBuffer ghost, ref NativeArray<SnapshotDataBuffer> snapshotDataBuffer, in DynamicBuffer<GhostCollectionPrefabSerializer> ghostTypeCollection)
        {
            var snapshotSize = ghostTypeCollection[ghost.GhostType].SnapshotSize;
            bool hasBuffers = ghostTypeCollection[ghost.GhostType].NumBuffers > 0;

            var entity = entityManager.CreateEntity();
#if !DOTS_DISABLE_DEBUG_NAMES
            entityManager.SetName(entity, $"GHOST-PLACEHOLDER-{ghost.GhostType}");
#endif
            entityManager.AddComponentData(entity, new GhostInstance { ghostId = ghost.GhostID, ghostType = ghost.GhostType, spawnTick = ghost.ServerSpawnTick });
            entityManager.AddComponent<PendingSpawnPlaceholder>(entity);
            if (PrespawnHelper.IsPrespawnGhostId(ghost.GhostID))
                ConfigurePrespawnGhost(ref entityManager, entity, ghost);

            var newBuffer = entityManager.AddBuffer<SnapshotDataBuffer>(entity);
            newBuffer.ResizeUninitialized(snapshotSize * GhostSystemConstants.SnapshotHistorySize);
            var snapshotData = (byte*)newBuffer.GetUnsafePtr();
            // 实体具有 Buffer 时还要添加 SnapshotDynamicDataBuffer，以复制动态内容
            if (hasBuffers)
                entityManager.AddBuffer<SnapshotDynamicDataBuffer>(entity);
            entityManager.AddComponentData(entity, new SnapshotData { SnapshotSize = snapshotSize, LatestIndex = 0 });

            delayedSpawnQueue.Enqueue(new GhostSpawnSystem.DelayedSpawnGhost { ghostId = ghost.GhostID, ghostType = ghost.GhostType, clientSpawnTick = ghost.ClientSpawnTick, serverSpawnTick = ghost.ServerSpawnTick, oldEntity = entity, predictedSpawnEntity = ghost.PredictedSpawnEntity });

            UnsafeUtility.MemClear(snapshotData, snapshotSize * GhostSystemConstants.SnapshotHistorySize);
            UnsafeUtility.MemCpy(snapshotData, (byte*)snapshotDataBuffer.GetUnsafeReadOnlyPtr() + ghost.DataOffset, snapshotSize);
            if (hasBuffers)
            {
                // 调整大小并复制关联的 Dynamic Buffer Snapshot 数据
                var snapshotDynamicBuffer = entityManager.GetBuffer<SnapshotDynamicDataBuffer>(entity);
                var dynamicDataCapacity = SnapshotDynamicBuffersHelper.CalculateBufferCapacity(ghost.DynamicDataSize, out var _);
                snapshotDynamicBuffer.ResizeUninitialized((int)dynamicDataCapacity);
                var dynamicSnapshotData = (byte*)snapshotDynamicBuffer.GetUnsafePtr();
                if (dynamicSnapshotData == null)
                    throw new InvalidOperationException("snapshot dynamic data buffer not initialized but ghost has dynamic buffer contents");

                // 将当前槽位的已用大小写入动态数据 Header，即 uint[GhostSystemConstants.SnapshotHistorySize]
                // 对新生成实体而言当前槽位为 0
                // 无需将所有 Header 槽位初始化为 0，因为这些信息只用于增量压缩
                // 且增量压缩依赖已 Ack Tick，通常只会访问已初始化且相关的槽位
                // 布局详情参见 SnapshotData.cs
                ((uint*)dynamicSnapshotData)[0] = ghost.DynamicDataSize;
                var headerSize = SnapshotDynamicBuffersHelper.GetHeaderSize();
                UnsafeUtility.MemCpy(dynamicSnapshotData + headerSize, (byte*)snapshotDataBuffer.GetUnsafeReadOnlyPtr() + ghost.DataOffset + snapshotSize, ghost.DynamicDataSize);
            }

            return entity;
        }

        unsafe bool TrySpawnFromDelayedQueue(ref EntityManager entityManager, in DelayedSpawnGhost ghost, GhostSpawnBuffer.Type spawnType, in NativeArray<GhostCollectionPrefab> prefabs, Entity ghostCollectionSingleton, out Entity entity)
        {
            entity = Entity.Null;

            // TODO：报错前可以为 Prefab 加载预留一段时间
            if (prefabs[ghost.ghostType].GhostPrefab == Entity.Null)
            {
                ReportMissingPrefab(ref entityManager);
                return false;
            }
            // 实体在队列等待期间已被销毁
            if (!entityManager.HasComponent<GhostInstance>(ghost.oldEntity))
                return false;

            // 生成实际实体
            entity = ghost.predictedSpawnEntity != Entity.Null ? ghost.predictedSpawnEntity : entityManager.Instantiate(prefabs[ghost.ghostType].GhostPrefab);
            if(entityManager.HasComponent<PredictedGhostSpawnRequest>(entity))
                entityManager.RemoveComponent<PredictedGhostSpawnRequest>(entity);
            if (entityManager.HasComponent<GhostPrefabMetaData>(prefabs[ghost.ghostType].GhostPrefab))
            {
                ref var toRemove = ref entityManager.GetComponentData<GhostPrefabMetaData>(prefabs[ghost.ghostType].GhostPrefab).Value.Value.DisableOnInterpolatedClient;
                if (spawnType == GhostSpawnBuffer.Type.Predicted)
                    toRemove = ref entityManager.GetComponentData<GhostPrefabMetaData>(prefabs[ghost.ghostType].GhostPrefab).Value.Value.DisableOnPredictedClient;
                var linkedEntityGroup = entityManager.GetBuffer<LinkedEntityGroup>(entity).ToNativeArray(Allocator.Temp);
                // 移除组件会产生结构变更并使 Buffer 指针失效，因此需要先复制 LinkedEntityGroup
                for (int rm = 0; rm < toRemove.Length; ++rm)
                {
                    var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(toRemove[rm].StableHash));
                    entityManager.RemoveComponent(linkedEntityGroup[toRemove[rm].EntityIndex].Value, compType);
                }
            }
            entityManager.SetComponentData(entity, entityManager.GetComponentData<SnapshotData>(ghost.oldEntity));
            if (PrespawnHelper.IsPrespawnGhostId(ghost.ghostId))
            {
                entityManager.AddComponentData(entity, entityManager.GetComponentData<PreSpawnedGhostIndex>(ghost.oldEntity));
                entityManager.AddSharedComponent(entity, entityManager.GetSharedComponent<SceneSection>(ghost.oldEntity));
            }
            var ghostComponentData = entityManager.GetComponentData<GhostInstance>(ghost.oldEntity);
            entityManager.SetComponentData(entity, ghostComponentData);
            var oldBuffer = entityManager.GetBuffer<SnapshotDataBuffer>(ghost.oldEntity);
            var newBuffer = entityManager.GetBuffer<SnapshotDataBuffer>(entity);
            newBuffer.ResizeUninitialized(oldBuffer.Length);
            UnsafeUtility.MemCpy(newBuffer.GetUnsafePtr(), oldBuffer.GetUnsafeReadOnlyPtr(), oldBuffer.Length);
            // 将旧 Buffer 内容复制到新实体
            // 性能 FIXME：如果能为 Buffer 引入类似 Move 的所有权转移机制，就可以避免大量复制和分配
            var ghostTypeCollection = entityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollectionSingleton);
            bool hasBuffers = ghostTypeCollection[ghost.ghostType].NumBuffers > 0;
            if (hasBuffers)
            {
                var oldDynamicBuffer = entityManager.GetBuffer<SnapshotDynamicDataBuffer>(ghost.oldEntity);
                var newDynamicBuffer = entityManager.GetBuffer<SnapshotDynamicDataBuffer>(entity);
                newDynamicBuffer.ResizeUninitialized(oldDynamicBuffer.Length);
                UnsafeUtility.MemCpy(newDynamicBuffer.GetUnsafePtr(), oldDynamicBuffer.GetUnsafeReadOnlyPtr(), oldDynamicBuffer.Length);
            }
            entityManager.DestroyEntity(ghost.oldEntity);

            return true;
        }
    }
}
