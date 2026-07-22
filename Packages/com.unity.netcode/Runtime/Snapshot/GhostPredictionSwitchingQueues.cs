using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Transforms;

namespace Unity.NetCode
{
    /// <summary>
    /// 包含将客户端 Ghost <see cref="GhostMode"/> 转换为 <see cref="GhostMode.Predicted"/> 或 <see cref="GhostMode.Interpolated"/> 所需 API 和集合的单例组件
    /// 参见 <see cref="GhostPredictionSwitchingSystem"/>
    /// </summary>
    public struct GhostPredictionSwitchingQueues : IComponentData
    {
        /// <summary>
        /// 参见 <see cref="PredictionSwitchingUtilities.ConvertGhostToPredicted"/>
        /// </summary>
        public NativeQueue<ConvertPredictionEntry>.ParallelWriter ConvertToPredictedQueue;
        /// <summary>
        /// 参见 <see cref="PredictionSwitchingUtilities.ConvertGhostToInterpolated"/>
        /// </summary>
        public NativeQueue<ConvertPredictionEntry>.ParallelWriter ConvertToInterpolatedQueue;
    }

    /// <summary>
    /// 存储 <see cref="GhostPredictionSwitchingQueues"/> 单个队列条目设置的结构
    /// </summary>
    [NoAlias]
    public struct ConvertPredictionEntry
    {
        /// <summary>
        /// 要转换的实体
        /// </summary>
        public Entity TargetEntity;

        /// <summary>
        /// 通过 <see cref="GhostPredictionSmoothing"/> 系统和 <see cref="SwitchPredictionSmoothing"/> 组件
        /// 对目标实体的 <see cref="LocalToWorld"/> 进行平滑
        /// 此值控制转换过渡的缓和程度，建议默认值为 1.0 秒
        /// 注意：过渡完成前也会阻止再次转换该 Ghost
        /// </summary>
        public float TransitionDurationSeconds;
    }

    /// <summary>
    /// 可按实体或按 Chunk 添加的可选组件
    /// 用于自定义从预测模式转换到插值 <see cref="GhostMode"/> 时的过渡时间
    /// 如果存在此组件，其 <see cref="TransitionDurationSeconds"/> 优先于
    /// 传给 <see cref="ConvertPredictionEntry.TransitionDurationSeconds"/> 的设置
    /// </summary>
    public struct PredictionSwitchingSmoothing : IComponentData
    {
        /// <inheritdoc cref="ConvertPredictionEntry.TransitionDurationSeconds"/>
        public float TransitionDurationSeconds;
    }

    /// <summary>
    /// 存储 <see cref="GhostOwnerPredictedSwitchingQueue"/> 队列条目设置的结构
    /// </summary>
    internal struct OwnerSwithchingEntry
    {
        /// <summary>
        /// <see cref="GhostOwner"/> 组件的当前值
        /// </summary>
        public int CurrentOwner;

        /// <summary>
        /// 新的 Ghost 所有者，可以是有效 <see cref="NetworkId"/>，也可以是 0 或负数等无效值
        /// </summary>
        public int NewOwner;

        /// <summary>
        /// 需要转换为预测或插值模式的 Ghost
        /// </summary>
        public Entity TargetEntity;
    }

    /// <summary>
    /// 用于跟踪 <see cref="GhostMode.OwnerPredicted"/> Ghost 所有者变化的单例组件
    /// 所有者变化后需要调整该 Ghost 在客户端上的模拟方式，具体规则如下
    /// <list type="bullet">
    /// <item>所有者与客户端 <see cref="NetworkId"/> 相同时，Ghost 转为预测模式</item>
    /// <item>所有者与客户端 <see cref="NetworkId"/> 不同时，Ghost 转为插值模式</item>
    /// </list>
    /// </summary>
    internal struct GhostOwnerPredictedSwitchingQueue : IComponentData
    {
        /// <summary>
        /// <see cref="GhostOwner"/> 已变化且需要转换为相应插值或预测版本的所有者预测 Ghost 列表
        /// </summary>
        public NativeQueue<OwnerSwithchingEntry> SwitchOwnerQueue;
    }

#if UNITY_EDITOR
    internal struct PredictionSwitchingAnalyticsData : IComponentData
    {
        public long NumTimesSwitchedToPredicted;
        public long NumTimesSwitchedToInterpolated;
        // TODO：需要修改 Analytics Schema 后才能上报此字段，参见 JIRA MTT-7267
        public long NumTimesSwitchedOwner;
    }
#endif

    /// <summary>
    /// 对 <see cref="GhostPredictionSwitchingQueues"/> 中排队实体应用预测模式切换的系统
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostReceiveSystem))]
    [UpdateBefore(typeof(GhostUpdateSystem))]
    public partial struct GhostPredictionSwitchingSystem : ISystem
    {
        NativeQueue<ConvertPredictionEntry> m_ConvertToInterpolatedQueue;
        NativeQueue<ConvertPredictionEntry> m_ConvertToPredictedQueue;
        NativeQueue<OwnerSwithchingEntry> m_OwnerPredictedQueue;
        ComponentLookup<PredictionSwitchingSmoothing> m_PredictionSwitchingSmoothingLookup;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
#if UNITY_EDITOR
            SetupAnalyticsSingleton(state.EntityManager);
#endif
            m_ConvertToInterpolatedQueue = new NativeQueue<ConvertPredictionEntry>(Allocator.Persistent);
            m_ConvertToPredictedQueue = new NativeQueue<ConvertPredictionEntry>(Allocator.Persistent);
            m_PredictionSwitchingSmoothingLookup = state.GetComponentLookup<PredictionSwitchingSmoothing>(true);
            m_OwnerPredictedQueue = new NativeQueue<OwnerSwithchingEntry>(Allocator.Persistent);
            var singletonEntity = state.EntityManager.CreateEntity(
                ComponentType.ReadOnly<GhostPredictionSwitchingQueues>(),
                ComponentType.ReadOnly<GhostOwnerPredictedSwitchingQueue>());
            state.EntityManager.SetName(singletonEntity, (FixedString64Bytes)"GhostPredictionQueues");
            SystemAPI.SetSingleton(new GhostPredictionSwitchingQueues
            {
                ConvertToInterpolatedQueue = m_ConvertToInterpolatedQueue.AsParallelWriter(),
                ConvertToPredictedQueue = m_ConvertToPredictedQueue.AsParallelWriter(),
            });
            SystemAPI.SetSingleton(new GhostOwnerPredictedSwitchingQueue
            {
                SwitchOwnerQueue = m_OwnerPredictedQueue
            });
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            m_ConvertToPredictedQueue.Dispose();
            m_ConvertToInterpolatedQueue.Dispose();
            m_OwnerPredictedQueue.Dispose();
        }

#if UNITY_EDITOR
        static void SetupAnalyticsSingleton(EntityManager entityManager)
        {
            entityManager.CreateSingleton<PredictionSwitchingAnalyticsData>();
        }
#endif

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // 在主线程检查这些队列前必须等待所有写入者完成
            state.CompleteDependency();
            FixedList64Bytes<Entity> batchedDeletedWarnings = default;
            uint batchedDeletedCount = 0;
            // 客户端未连接服务器或尚未进入游戏时，队列应为空
            // 最坏情况下客户端断开连接，此时 GhostReceiveSystem 已销毁所有实体
            // ConvertOwnerPredictedGhost 会检测这种情况，因此不会造成错误
            // 但客户端不在游戏中时，应跳过转换或所有者切换请求并清空队列
            if (!SystemAPI.HasSingleton<NetworkStreamInGame>())
            {
                m_ConvertToPredictedQueue.Clear();
                m_ConvertToInterpolatedQueue.Clear();
                m_OwnerPredictedQueue.Clear();
                return;
            }
            if (m_ConvertToPredictedQueue.Count + m_ConvertToInterpolatedQueue.Count + m_OwnerPredictedQueue.Count > 0)
            {
#if UNITY_EDITOR
                UpdateAnalyticsSwitchCount();
#endif
                var netDebug = SystemAPI.GetSingleton<NetDebug>();
                var ghostUpdateVersion = SystemAPI.GetSingleton<GhostUpdateVersion>();
                var prefabs = SystemAPI.GetSingletonBuffer<GhostCollectionPrefab>().ToNativeArray(Allocator.Temp);
                var networkId = SystemAPI.GetSingleton<NetworkId>();
                while (m_OwnerPredictedQueue.TryDequeue(out var ownerSwitching))
                {
                    // 添加和移除组件会使 Lookup 的安全句柄失效，因此必须在每次结构变更后更新 Lookup
                    // 这主要是底层安全限制，行为上接近一个缺陷
                    m_PredictionSwitchingSmoothingLookup.Update(ref state);
                    m_PredictionSwitchingSmoothingLookup.TryGetComponent(ownerSwitching.TargetEntity, out var smoothing);
                    PredictionSwitchingUtilities.ConvertOwnerPredictedGhost(state.EntityManager,
                        ownerSwitching.TargetEntity, ownerSwitching.NewOwner, networkId.Value,
                        ghostUpdateVersion, netDebug, prefabs,
                        smoothing.TransitionDurationSeconds, ref batchedDeletedWarnings, ref batchedDeletedCount);
                }
                while (m_ConvertToPredictedQueue.TryDequeue(out var conversion))
                {
                    PredictionSwitchingUtilities.ConvertGhostToPredicted(state.EntityManager, ghostUpdateVersion, netDebug, prefabs, conversion.TargetEntity, conversion.TransitionDurationSeconds, ref batchedDeletedWarnings, ref batchedDeletedCount);
                }
                while (m_ConvertToInterpolatedQueue.TryDequeue(out var conversion))
                {
                    PredictionSwitchingUtilities.ConvertGhostToInterpolated(state.EntityManager, ghostUpdateVersion, netDebug, prefabs, conversion.TargetEntity, conversion.TransitionDurationSeconds, ref batchedDeletedWarnings, ref batchedDeletedCount);
                }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (batchedDeletedWarnings.Length > 0)
                {
                    FixedString512Bytes batchedWarning = $"Failed to 'switch prediction' on {batchedDeletedCount} entities as they don't exist! Likely destroyed after added to the queue. Subset of destroyed entities:[";
                    foreach (var entity in batchedDeletedWarnings)
                    {
                        batchedWarning.Append(entity.ToFixedString());
                        batchedWarning.Append(',');
                    }
                    if (batchedDeletedWarnings.Length == batchedWarning.Capacity)
                        batchedWarning.Append((FixedString32Bytes)"etc");
                    batchedWarning.Append((FixedString32Bytes)"].");
                    netDebug.DebugLog(batchedWarning);
                }
#endif
            }
        }

#if UNITY_EDITOR
        void UpdateAnalyticsSwitchCount()
        {
            ref var analyticsData = ref SystemAPI.GetSingletonRW<PredictionSwitchingAnalyticsData>().ValueRW;
            analyticsData.NumTimesSwitchedToPredicted += m_ConvertToPredictedQueue.Count;
            analyticsData.NumTimesSwitchedToInterpolated += m_ConvertToInterpolatedQueue.Count;
            analyticsData.NumTimesSwitchedOwner += m_OwnerPredictedQueue.Count;
        }
#endif
    }

    static internal class PredictionSwitchingUtilities
    {
        /// <summary>
        /// 根据所有者将所有者预测 Ghost 转换为插值或预测 Ghost
        /// 该 Ghost 必须同时支持插值和预测模式
        /// 此操作新增的组件会使用 Ghost Prefab 中的初始值
        /// </summary>
        static public void ConvertOwnerPredictedGhost(EntityManager entityManager,
            Entity entity, int newOwner, int localNetworkId,
            GhostUpdateVersion ghostUpdateVersion, NetDebug netDbg, NativeArray<GhostCollectionPrefab> ghostCollectionPrefabs,
            float transitionDuration,
            ref FixedList64Bytes<Entity> destroyedEntities, ref uint batchedDeletedCount)
        {
            if (!entityManager.Exists(entity))
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if(destroyedEntities.Length < destroyedEntities.Capacity)
                    destroyedEntities.Add(entity);
                batchedDeletedCount++;
#endif
                return;
            }

            if (!entityManager.HasComponent<GhostInstance>(entity))
            {
                netDbg.LogError($"Trying to switch owner for an owner-predicted ghost, but this is not a ghost entity! {entity.ToFixedString()}");
                return;
            }
            if (entityManager.HasComponent<Prefab>(entity))
            {
                netDbg.LogError($"Trying to switch owner for an owner-predicted ghost, but this is a prefab! {entity.ToFixedString()}");
                return;
            }
            var ghost = entityManager.GetComponentData<GhostInstance>(entity);
            var prefab = ghostCollectionPrefabs[ghost.ghostType].GhostPrefab;
            if (!entityManager.HasComponent<GhostPrefabMetaData>(prefab))
            {
                netDbg.LogWarning($"Trying to switch owner for an owner-predicted ghost, but did not find a prefab with meta data! {entity.ToFixedString()}");
                return;
            }
            ref var ghostMetaData = ref entityManager.GetComponentData<GhostPrefabMetaData>(prefab).Value.Value;
            if (ghostMetaData.SupportedModes != GhostPrefabBlobMetaData.GhostMode.Both)
            {
                netDbg.LogWarning($"Trying to switch owner for an owner-predicted ghost, but do not support switching modes! {entity.ToFixedString()}");
                return;
            }
            if (ghostMetaData.DefaultMode != GhostPrefabBlobMetaData.GhostMode.Both)
            {
                netDbg.LogWarning($"Trying to convert a ghost that is not owner-predicted using the owner-switch queue, that is not allowed!. {entity.ToFixedString()}");
                return;
            }
            bool isPredicted = entityManager.HasComponent<PredictedGhost>(entity);
            if (localNetworkId == newOwner && !isPredicted)
            {
                ref var toAdd = ref ghostMetaData.DisableOnInterpolatedClient;
                ref var toRemove = ref ghostMetaData.DisableOnPredictedClient;
                AddRemoveComponents(entityManager, ref ghostUpdateVersion, entity, prefab, ref toAdd, ref toRemove, transitionDuration);
            }
            else if(localNetworkId != newOwner && isPredicted)
            {
                ref var toAdd = ref ghostMetaData.DisableOnPredictedClient;
                ref var toRemove = ref ghostMetaData.DisableOnInterpolatedClient;
                AddRemoveComponents(entityManager, ref ghostUpdateVersion, entity, prefab, ref toAdd, ref toRemove, transitionDuration);
            }
        }

        /// <summary>
        /// 将插值 Ghost 转换为预测 Ghost
        /// 该 Ghost 必须同时支持插值和预测模式，并且不能是所有者预测 Ghost
        /// 此操作新增的组件会使用 Ghost Prefab 中的初始值
        /// </summary>
        static public void ConvertGhostToPredicted(EntityManager entityManager, GhostUpdateVersion ghostUpdateVersion,
            NetDebug netDbg, NativeArray<GhostCollectionPrefab> ghostCollectionPrefabs, Entity entity, float transitionDuration,
            ref FixedList64Bytes<Entity> destroyedEntities, ref uint batchedDeletedCount)
        {
            if (!entityManager.Exists(entity))
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if(destroyedEntities.Length < destroyedEntities.Capacity)
                    destroyedEntities.Add(entity);
                batchedDeletedCount++;
#endif
                return;
            }
            if (!entityManager.HasComponent<GhostInstance>(entity))
            {
                netDbg.LogError($"Trying to convert a ghost to predicted, but this is not a ghost entity! {entity.ToFixedString()}");
                return;
            }
            if (entityManager.HasComponent<Prefab>(entity))
            {
                netDbg.LogError($"Trying to convert a ghost to predicted, but this is a prefab! {entity.ToFixedString()}");
                return;
            }
            if (entityManager.HasComponent<PredictedGhost>(entity))
            {
                netDbg.LogWarning($"Trying to convert a ghost to predicted, but it is already predicted! {entity.ToFixedString()}");
                return;
            }
            var ghost = entityManager.GetComponentData<GhostInstance>(entity);
            var prefab = ghostCollectionPrefabs[ghost.ghostType].GhostPrefab;
            if (!entityManager.HasComponent<GhostPrefabMetaData>(prefab))
            {
                netDbg.LogWarning($"Trying to convert a ghost to predicted, but did not find a prefab with meta data! {entity.ToFixedString()}");
                return;
            }
            ref var ghostMetaData = ref entityManager.GetComponentData<GhostPrefabMetaData>(prefab).Value.Value;
            if (ghostMetaData.SupportedModes != GhostPrefabBlobMetaData.GhostMode.Both)
            {
                netDbg.LogWarning($"Trying to convert a ghost to predicted, but it does not support both modes! {entity.ToFixedString()}");
                return;
            }
            if (ghostMetaData.DefaultMode == GhostPrefabBlobMetaData.GhostMode.Both)
            {
                netDbg.LogWarning($"Trying to convert a ghost to predicted, but it is owner predicted and owner predicted ghosts cannot be switched on demand! You must queue a owner-switching change using the GhostOwnerPredictedSwitchingQueue. {entity.ToFixedString()}");
                return;
            }

            ref var toAdd = ref ghostMetaData.DisableOnInterpolatedClient;
            ref var toRemove = ref ghostMetaData.DisableOnPredictedClient;
            AddRemoveComponents(entityManager, ref ghostUpdateVersion, entity, prefab, ref toAdd, ref toRemove, transitionDuration);
        }

        /// <summary>
        /// 将预测 Ghost 转换为插值 Ghost
        /// 该 Ghost 必须同时支持插值和预测模式，并且不能是所有者预测 Ghost
        /// 此操作新增的组件会使用 Ghost Prefab 中的初始值
        /// </summary>
        static public void ConvertGhostToInterpolated(EntityManager entityManager, GhostUpdateVersion ghostUpdateVersion, NetDebug netDbg, NativeArray<GhostCollectionPrefab> ghostCollectionPrefabs, Entity entity, float transitionDuration, ref FixedList64Bytes<Entity> destroyedEntities, ref uint batchedDeletedCount)
        {
            if (!entityManager.Exists(entity))
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if(destroyedEntities.Length < destroyedEntities.Capacity)
                    destroyedEntities.Add(entity);
                batchedDeletedCount++;
#endif
                return;
            }
            if (!entityManager.HasComponent<GhostInstance>(entity))
            {
                netDbg.LogError($"Trying to convert a ghost to interpolated, but this is not a ghost entity! {entity.ToFixedString()}");
                return;
            }
            if (entityManager.HasComponent<Prefab>(entity))
            {
                netDbg.LogError($"Trying to convert a ghost to interpolated, but this is a prefab! {entity.ToFixedString()}");
                return;
            }
            if (!entityManager.HasComponent<PredictedGhost>(entity))
            {
                netDbg.LogWarning($"Trying to convert a ghost to interpolated, but it is already interpolated! {entity.ToFixedString()}");
                return;
            }

            var ghost = entityManager.GetComponentData<GhostInstance>(entity);
            var prefab = ghostCollectionPrefabs[ghost.ghostType].GhostPrefab;
            if (!entityManager.HasComponent<GhostPrefabMetaData>(prefab))
            {
                netDbg.LogWarning($"Trying to convert a ghost to interpolated, but did not find a prefab with meta data! {entity.ToFixedString()}");
                return;
            }

            ref var ghostMetaData = ref entityManager.GetComponentData<GhostPrefabMetaData>(prefab).Value.Value;
            if (ghostMetaData.SupportedModes != GhostPrefabBlobMetaData.GhostMode.Both)
            {
                netDbg.LogWarning($"Trying to convert a ghost to interpolated, but it does not support both modes! {entity.ToFixedString()}");
                return;
            }
            if (ghostMetaData.DefaultMode == GhostPrefabBlobMetaData.GhostMode.Both)
            {
                netDbg.LogWarning($"Trying to convert a ghost to interpolated, but it is owner predicted and owner predicted ghosts cannot be switched on demand! You must queue a owner-switching change using the GhostOwnerPredictedSwitchingQueue. {entity.ToFixedString()}");
                return;
            }

            ref var toAdd = ref ghostMetaData.DisableOnPredictedClient;
            ref var toRemove = ref ghostMetaData.DisableOnInterpolatedClient;
            AddRemoveComponents(entityManager, ref ghostUpdateVersion, entity, prefab, ref toAdd, ref toRemove, transitionDuration);
        }

        static unsafe void AddRemoveComponents(EntityManager entityManager, ref GhostUpdateVersion ghostUpdateVersion, Entity entity, Entity prefab, ref BlobArray<GhostPrefabBlobMetaData.ComponentReference> toAdd, ref BlobArray<GhostPrefabBlobMetaData.ComponentReference> toRemove, float duration)
        {
            var linkedEntityGroup = entityManager.GetBuffer<LinkedEntityGroup>(entity).ToNativeArray(Allocator.Temp);
            var prefabLinkedEntityGroup = entityManager.GetBuffer<LinkedEntityGroup>(prefab).ToNativeArray(Allocator.Temp);
            // 移除组件会产生结构变更并使 Buffer 指针失效，因此需要先复制 LinkedEntityGroup
            for (int add = 0; add < toAdd.Length; ++add)
            {
                var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(toAdd[add].StableHash));
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (compType.IsChunkComponent || compType.IsSharedComponent)
                {
                    throw new InvalidOperationException($"Ghosts with chunk or shared components cannot switch prediction. {entity.ToFixedString()}");
                }
#endif
                // TODO：研究分两轮批量执行 AddComponent
                entityManager.AddComponent(linkedEntityGroup[toAdd[add].EntityIndex].Value, compType);
                if (compType.IsZeroSized)
                    continue;
                var typeInfo = TypeManager.GetTypeInfo(compType.TypeIndex);
                var typeHandle = entityManager.GetDynamicComponentTypeHandle(compType);
                var sizeInChunk = typeInfo.SizeInChunk;
                var srcInfo = entityManager.GetStorageInfo(prefabLinkedEntityGroup[toAdd[add].EntityIndex].Value);
                var dstInfo = entityManager.GetStorageInfo(linkedEntityGroup[toAdd[add].EntityIndex].Value);
                if (compType.IsBuffer)
                {
                    var srcBufferChunkAccessor = srcInfo.Chunk.GetUntypedBufferAccessor(ref typeHandle);
                    var dstBufferChunkAccessor = dstInfo.Chunk.GetUntypedBufferAccessor(ref typeHandle);
                    // srcBuffer.Length 表示该 Chunk 中的实体数量，此处需要的是 srcPrefabBufferLength
                    var srcDataPtr = srcBufferChunkAccessor.GetUnsafeReadOnlyPtrAndLength(srcInfo.IndexInChunk, out var srcPrefabBufferLength);
                    dstBufferChunkAccessor.ResizeUninitialized(dstInfo.IndexInChunk, srcPrefabBufferLength); // 调整实体 Buffer 大小以容纳 Prefab 的全部原始 Buffer 元素
                    var dstDataPtr = dstBufferChunkAccessor.GetUnsafeReadOnlyPtr(dstInfo.IndexInChunk);
                    UnsafeUtility.MemCpy(dstDataPtr, srcDataPtr, typeInfo.ElementSize * srcPrefabBufferLength);
                }
                else
                {
                    byte* src = (byte*)srcInfo.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandle, sizeInChunk).GetUnsafeReadOnlyPtr();
                    byte* dst = (byte*)dstInfo.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandle, sizeInChunk).GetUnsafePtr();
                    UnsafeUtility.MemCpy(dst + dstInfo.IndexInChunk*sizeInChunk, src + srcInfo.IndexInChunk*sizeInChunk, sizeInChunk);
                }
            }
            for (int rm = 0; rm < toRemove.Length; ++rm)
            {
                // TODO：研究分两轮批量执行 RemoveComponent
                var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(toRemove[rm].StableHash));
                entityManager.RemoveComponent(linkedEntityGroup[toRemove[rm].EntityIndex].Value, compType);
            }
            if (duration > 0 &&
                entityManager.HasComponent<LocalToWorld>(entity) &&
                entityManager.HasComponent<LocalTransform>(entity))
            {
                entityManager.AddComponent(entity, new ComponentTypeSet(ComponentType.ReadWrite<SwitchPredictionSmoothing>()));
                var localTransform = entityManager.GetComponentData<LocalTransform>(entity);
                entityManager.SetComponentData(entity, new SwitchPredictionSmoothing
                {
                    InitialPosition = localTransform.Position,
                    InitialRotation = localTransform.Rotation,
                    CurrentFactor = 0,
                    Duration = duration,
                    SkipVersion = ghostUpdateVersion.LastSystemVersion
                });
            }
        }
    }
}
