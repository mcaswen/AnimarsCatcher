using System;
using System.Diagnostics;
using Unity.Assertions;
using Unity.Entities;
using Unity.Collections;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Transforms;

namespace Unity.NetCode
{
    struct GhostPredictionGroupTickState : IComponentData
    {
        public NativeParallelHashMap<NetworkTick, NetworkTick> AppliedPredictedTicks;
    }

    /// <summary>
    /// <para>仅存在于客户端 World 中，负责以下工作：</para>
    /// <para>- 从接收到的 Snapshot 复制并插值数据，以更新插值 Ghost 的状态</para>
    /// <para>- 在运行下一轮预测循环前，从 <see cref="GhostPredictionHistoryState"/> 恢复预测 Ghost 的状态，直到收到新的 Snapshot</para>
    /// <para>- 根据最新收到的 Snapshot 更新所有预测 Ghost 的 <see cref="PredictedGhost"/> 属性，包括反映最新已应用 Snapshot 的 <see cref="PredictedGhost.AppliedTick"/>
    /// 以及设置 Ghost 开始预测的正确 Tick，参见 <see cref="PredictedGhost.PredictionStartTick"/></para>
    /// </summary>
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostReceiveSystem))]
    [UpdateBefore(typeof(GhostSpawnClassificationSystemGroup))]
    [UpdateBefore(typeof(GhostInputSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [BurstCompile]
    public unsafe partial struct GhostUpdateSystem : ISystem
    {
        // 使用泛型 Job 结构体会产生 Burst 或 IL 问题，因此在这里手动列出每种 Job 大小类型
        [BurstCompile]
        struct UpdateJob : IJobChunk
        {
            public DynamicTypeList DynamicTypeList;
            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferLookup<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostComponentSerializer.State> GhostComponentCollection;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostCollectionPrefabSerializer> GhostTypeCollection;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostCollectionComponentIndex> GhostComponentIndex;
            [ReadOnly] public NativeHashMap<GhostType, int>.ReadOnly GhostTypeToCollectionIndex;

            [ReadOnly] public NativeParallelHashMap<SpawnedGhost, Entity>.ReadOnly GhostMap;
    #if UNITY_EDITOR || NETCODE_DEBUG
            [NativeDisableParallelForRestriction] public NativeArray<NetworkTick> minMaxSnapshotTick;
    #endif
    #pragma warning disable 649
            [NativeSetThreadIndex] public int ThreadIndex;
    #pragma warning restore 649
            [ReadOnly] public ComponentTypeHandle<GhostInstance> ghostInstanceTypeHandle;
            [ReadOnly] public ComponentTypeHandle<SnapshotData> ghostSnapshotDataType;
            [ReadOnly] public BufferTypeHandle<SnapshotDataBuffer> ghostSnapshotDataBufferType;
            [ReadOnly] public BufferTypeHandle<SnapshotDynamicDataBuffer> ghostSnapshotDynamicDataBufferType;
            [ReadOnly] public ComponentTypeHandle<PreSpawnedGhostIndex> prespawnGhostIndexType;
            [ReadOnly] public ComponentTypeHandle<PredictedGhostSpawnRequest> predictedGhostRequestType;
            [ReadOnly] public ComponentTypeHandle<GhostType> ghostTypeHandle;

            public NetworkTick interpolatedTargetTick;
            public float interpolatedTargetTickFraction;
            public NetworkTick predictedTargetTick;
            public float predictedTargetTickFraction;

            public NativeParallelHashMap<NetworkTick, NetworkTick>.ParallelWriter appliedPredictedTicks;
            [ReadOnly]public NativeArray<int> numPredictedGhostWithNewData;
            public ComponentTypeHandle<PredictedGhost> PredictedGhostType;
            public NetworkTick lastPredictedTick;
            public NetworkTick lastInterpolatedTick;

            [ReadOnly] public EntityStorageInfoLookup childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;

            public NetworkTick predictionStateBackupTick;
            public NativeParallelHashMap<ArchetypeChunk, System.IntPtr>.ReadOnly predictionStateBackup;
            public NativeParallelHashMap<Entity, GhostPredictionHistorySystem.PredictionBufferHistoryData>.ReadOnly predictionBackupEntityState;
            [ReadOnly] public EntityTypeHandle entityType;
            public int ghostOwnerId;
            public uint MaxExtrapolationTicks;
            public NetDebug netDebug;

            private void AddPredictionStartTick(NetworkTick targetTick, NetworkTick predictionStartTick)
            {
                // 加入 Ghost 开始预测的 Tick，但不能把起始 Tick 设为不早于目标 Tick 的值
                // 这种情况下无需预测，而起始 Tick 比目标 Tick 更新还可能导致循环一直执行到 uint 回绕
                // Buffer 中通常不会出现比目标 Tick 更新的 Tick，但时间失去同步且无法及时校正时仍可能发生
                if (targetTick.IsNewerThan(predictionStartTick))
                {
                    // 预测循环不会运行超过输入历史所覆盖的 Tick 数，因此限制起始 Tick 以控制 HashMap 的最大容量
                    var startTick = predictionStartTick;
                    if ((uint)targetTick.TicksSince(startTick) > CommandDataUtility.k_CommandDataMaxSize)
                    {
                        startTick = targetTick;
                        startTick.Subtract(CommandDataUtility.k_CommandDataMaxSize);
                    }
                    appliedPredictedTicks.TryAdd(startTick, predictionStartTick);
                }
            }
            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private static void ValidateReadEnableBits(int enableableMaskOffset, int numEnableBits)
            {
                if(enableableMaskOffset > numEnableBits)
                    throw new InvalidOperationException($"Read only {enableableMaskOffset} enable bits data whics are less than the expected {numEnableBits} for this ghost type. This is not a serializarion error but a problem restoring the component state from the decoded snapshot data.");
            }
            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private void ValidateAllEnableBitsHasBeenRead(int enableableMaskOffset, int numEnableBits)
            {
                if (enableableMaskOffset != numEnableBits)
                    throw new InvalidOperationException($"Read only {enableableMaskOffset} enable bits but expected to read exacly {numEnableBits} for this ghost type");
            }

            struct BackupRange
            {
                public int ent;
                public int indexInBackup;
                public IntPtr backupState;
            }

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // 此 Job 不支持包含可启用 Component 类型的查询
                Assert.IsFalse(useEnabledMask);

                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr = DynamicTypeList.GetData();
                int ghostChunkComponentTypesLength = DynamicTypeList.Length;
                GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];
                GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];

                bool predicted = chunk.Has(ref PredictedGhostType);
                NetworkTick targetTick = predicted ? predictedTargetTick : interpolatedTargetTick;
                float targetTickFraction = predicted ? predictedTargetTickFraction : interpolatedTargetTickFraction;

                var deserializerState = new GhostDeserializerState
                {
                    GhostMap = GhostMap,
                    GhostOwner = ghostOwnerId,
                    SendToOwner = SendToOwnerType.All
                };
                var ghostComponents = chunk.GetNativeArray(ref ghostInstanceTypeHandle);
                var ghostTypes = chunk.GetNativeArray(ref ghostTypeHandle);
                var ghostTypeId = ghostComponents[0].ghostType;
                if (chunk.Has(ref predictedGhostRequestType) || chunk.Has(ref prespawnGhostIndexType))
                {
                    // 检查预生成 Ghost 和预测 Ghost 是否具有有效的 Prefab 与 Serializer
                    // 如果无效则跳过该 Chunk
                    var ghostType = ghostTypes[0];
                    if (!GhostTypeToCollectionIndex.TryGetValue(ghostType, out ghostTypeId))
                        return;
                }
                // 序列化数据尚未加载完成，这可能发生在 Prefab 已加载但还未处理时
                // 例如 GhostCollectionPrefab 中排在前面的 Prefab 仍然缺失
                if (ghostTypeId >= GhostTypeCollection.Length)
                    return;
                var typeData = GhostTypeCollection[ghostTypeId];
                var ghostSnapshotDataArray = chunk.GetNativeArray(ref ghostSnapshotDataType);
                var ghostSnapshotDataBufferArray = chunk.GetBufferAccessor(ref ghostSnapshotDataBufferType);
                var ghostSnapshotDynamicBufferArray = chunk.GetBufferAccessor(ref ghostSnapshotDynamicDataBufferType);

                int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                int enableableMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.EnableableBits);

                int headerSize = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) + changeMaskUints*sizeof(uint) + enableableMaskUints*sizeof(uint));
                int snapshotDataOffset = headerSize;

                int snapshotDataAtTickSize = UnsafeUtility.SizeOf<SnapshotData.DataAtTick>();
#if UNITY_EDITOR || NETCODE_DEBUG
                var minMaxOffset = ThreadIndex * (JobsUtility.CacheLineSize/sizeof(int));
#endif
                var dataAtTick = new NativeArray<SnapshotData.DataAtTick>(ghostComponents.Length, Allocator.Temp);
                var entityRange = new NativeList<int2>(ghostComponents.Length, Allocator.Temp);
                int2 nextRange = default;
                var PredictedGhostArray = chunk.GetNativeArray(ref PredictedGhostType);
                bool isPrespawn = chunk.Has(ref prespawnGhostIndexType);
                var restoreFromBackupRange = new NativeList<BackupRange>(ghostComponents.Length, Allocator.Temp);
                var chunkEntities = chunk.GetNativeArray(entityType);

                int shouldRewindAndResimulate = 0;
                if (typeData.PredictedSpawnedGhostRollbackToSpawnTick != 0)
                {
                    for (int i = 0; i < JobsUtility.ThreadIndexCount; ++i)
                        shouldRewindAndResimulate += numPredictedGhostWithNewData[i*JobsUtility.CacheLineSize/sizeof(int)];
                }
                // 找出存在待应用数据的 Entity 区间，并在查找过程中把待应用数据存入数组
                for (int ent = 0; ent < ghostComponents.Length; ++ent)
                {
                    // 预生成 Ghost 可能尚未设置 Ghost 类型，此时需要跳过，直到 GhostReceiveSystem 为其分配类型
                    if (isPrespawn && ghostComponents[ent].ghostType != ghostTypeId)
                    {
                        if (nextRange.y != 0)
                            entityRange.Add(nextRange);
                        nextRange = default;
                        continue;
                    }
#if UNITY_EDITOR || NETCODE_DEBUG
                    // 验证 Ghost Entity 是由客户端预测生成，或因收到 Ghost 而生成
                    // 无论哪种情况，都要确认 Ghost Component 包含与当前 Entity 对应的数据
                    if((ghostComponents[ent].ghostId == 0) && (isPrespawn || !ghostComponents[ent].spawnTick.IsValid))
                    {
                        var invalidEntity = chunk.GetNativeArray(entityType)[ent];
                        if (isPrespawn)
                            netDebug.LogError($"Entity {invalidEntity.ToFixedString()} is not a valid prespawned ghost (ghostId == {ghostComponents[ent].ghostId}).");
                        else
                            netDebug.LogError($"Entity {invalidEntity.ToFixedString()} is not a valid ghost (ghostId == {ghostComponents[ent].ghostId}) (i.e. it is not a real 'replicated ghost', nor is it a 'predicted spawn' ghost). This can happen if you instantiate a ghost entity on the client manually (without marking it as a predicted spawn).");
                        // 跳过该 Entity
                        if (nextRange.y != 0)
                            entityRange.Add(nextRange);
                        nextRange = default;
                        continue;
                    }
#endif
                    // GhostId == 0 表示这是预测生成的 Ghost
                    // TODO: 可考虑使用 GhostId 的若干高位或低位标识预测生成
                    var snapshotDataBuffer = ghostSnapshotDataBufferArray[ent];
                    var ghostSnapshotData = ghostSnapshotDataArray[ent];
                    var latestTick = ghostSnapshotData.GetLatestTick(snapshotDataBuffer);
                    bool isStatic = typeData.CanBeStaticOptimized();
#if UNITY_EDITOR || NETCODE_DEBUG
                    if (latestTick.IsValid && !isStatic)
                    {
                        if (!minMaxSnapshotTick[minMaxOffset].IsValid || minMaxSnapshotTick[minMaxOffset].IsNewerThan(latestTick))
                            minMaxSnapshotTick[minMaxOffset] = latestTick;
                        if (!minMaxSnapshotTick[minMaxOffset + 1].IsValid || latestTick.IsNewerThan(minMaxSnapshotTick[minMaxOffset + 1]))
                            minMaxSnapshotTick[minMaxOffset + 1] = latestTick;
                    }
#endif

                    // 预测 Ghost 通常不会具有预测 Tick 对应的 Snapshot，以下情况除外：
                    // - 客户端落后于服务器
                    // - 预测 Tick 发生回滚
                    // - 启用了强制输入延迟
                    // 此方法开销较大，内部需要执行多项逻辑以获取：
                    // - 目标 Tick 前后已接收 Snapshot 的 Tick 与索引
                    bool hasSnapshot = ghostSnapshotData.GetDataAtTick(targetTick, typeData.PredictionOwnerOffset, ghostOwnerId,
                        targetTickFraction, snapshotDataBuffer, out var data, MaxExtrapolationTicks);
                    if (!hasSnapshot)
                    {
                        // 此处开销也较大，通常会执行两次线性搜索；单次并不严重，但持续对所有 Ghost 执行会产生明显负担
                        // 如果目标 Tick 之前没有 Snapshot，则尝试获取并使用现存最旧的 Tick
                        // 这样能更好地处理 Tick 后退，并把 Ghost 限制在仍有数据的最旧状态
                        var oldestSnapshot = ghostSnapshotData.GetOldestTick(snapshotDataBuffer);
                        hasSnapshot = (oldestSnapshot.IsValid && ghostSnapshotData.GetDataAtTick(oldestSnapshot, typeData.PredictionOwnerOffset, ghostOwnerId, 1, snapshotDataBuffer, out data, MaxExtrapolationTicks));
                    }
                    if (hasSnapshot)
                    {
                        if (predicted)
                        {
                            // 结果可能是目标 Tick 前后两个 Tick 之间的插值，但这里必须应用目标之前的 Tick，因此将插值系数设为 0
                            data.InterpolationFactor = 0;
                            var snapshotTick = new NetworkTick{SerializedData = *(uint*)data.SnapshotBefore};
                            var predictedData = PredictedGhostArray[ent];
                            // 尝试从上次完成预测的最后一个完整 Tick 继续预测
                            var predictionStartTick = predictionStateBackupTick;
                            // 如果没有历史记录，则尝试使用上次停止处的 Tick；只有上次结束于完整预测 Tick 而非部分 Tick 时该值才有效
                            if (!predictionStartTick.IsValid)
                                predictionStartTick = lastPredictedTick;
                            var hasBackup = predictionStartTick.IsValid;
                            // 如果没有备份，或上次运行后收到了更多数据，则从具有 Snapshot 数据的 Tick 开始
                            if (!hasBackup || predictedData.AppliedTick != snapshotTick)
                                predictionStartTick = snapshotTick;
                            // 如果 Snapshot Buffer 中存在更新或同样新的数据，则改为从新数据开始
                            else if (!predictionStartTick.IsNewerThan(snapshotTick))
                                predictionStartTick = snapshotTick;

                            // 如果备份可用、需要继续预测且上一个预测 Tick 是完整 Tick，则应尝试从备份恢复
                            // 这种情况下可以避免回滚
                            bool continuePrediction = predictionStartTick != snapshotTick;

                            // 对于 GhostId 为 0 的预测生成 Ghost，如果用户选择始终从生成 Tick 重新预测
                            // 且至少还有另一个预测 Ghost 将要回滚，则始终遵循该设置
                            // 否则在备份可用时尝试从备份继续预测
                            if (ghostComponents[ent].ghostId == 0 && shouldRewindAndResimulate != 0)
                            {
                                // 强制回退到 PredictedGhostSpawnSystem 保存在 Snapshot Buffer 中的 Snapshot Tick
                                predictionStartTick = snapshotTick;
                                continuePrediction = false;
                            }

                            // 优化：如果需要继续预测，且上一个 Tick 是完整 Tick，则当前状态将与备份完全一致
                            // 此时跳过备份恢复以节省 CPU
                            // 注意：未启用垂直同步的客户端较少遇到这种情况，但在移动设备或以固定 Tick Rate
                            // 运行的垂直同步设备上，这是常态，因此备份几乎不会被使用
                            // 在这些场景中，创建备份本身也会无谓消耗 CPU 时间
                            var restoreFromBackup = continuePrediction && (!lastPredictedTick.IsValid || predictionStartTick != lastPredictedTick);
                            if (restoreFromBackup)
                            {
                                // 如果无法恢复备份并继续预测，则回滚后重新模拟
                                if (TryGetChunkBackupState(chunk, ent, typeData.RollbackPredictionOnStructuralChanges,
                                        chunkEntities[ent], out var backupState, out var indexInBackup))
                                {
                                    restoreFromBackupRange.Add(new BackupRange
                                    {
                                        ent = ent,
                                        indexInBackup = indexInBackup,
                                        backupState = backupState
                                    });
                                }
                                else
                                {
                                    predictionStartTick = snapshotTick;
                                    continuePrediction = false;
                                }
                            }

                            AddPredictionStartTick(targetTick, predictionStartTick);

                            continuePrediction |= predictionStartTick == lastPredictedTick;

                            if (continuePrediction)
                            {
                                if (nextRange.y != 0)
                                    entityRange.Add(nextRange);
                                nextRange = default;
                            }
                            else
                            {
                                predictedData.AppliedTick = snapshotTick;
                                if (nextRange.y == 0)
                                    nextRange.x = ent;
                                nextRange.y = ent+1;
                            }
                            predictedData.PredictionStartTick = predictionStartTick;
                            PredictedGhostArray[ent] = predictedData;
                        }
                        else
                        {
                            // 如果该 Snapshot 是静态的，且最新 Tick 数据已在上次插值更新中应用，则可以跳过数据复制
                            // 注意：这也会禁用静态优化插值 Ghost 的外推
                            if (isStatic && latestTick.IsValid && lastInterpolatedTick.IsValid && !latestTick.IsNewerThan(lastInterpolatedTick))
                            {
                                if (nextRange.y != 0)
                                    entityRange.Add(nextRange);
                                nextRange = default;
                            }
                            else
                            {
                                if (nextRange.y == 0)
                                    nextRange.x = ent;
                                nextRange.y = ent+1;
                            }
                        }
                        dataAtTick[ent] = data;
                    }
                    else
                    {
                        if (nextRange.y != 0)
                        {
                            entityRange.Add(nextRange);
                            nextRange = default;
                        }
                        if (predicted)
                        {
                            // 预测模式的预生成 Ghost 在收到服务器首个 Snapshot 前可能没有有效 Snapshot
                            // 静态优化的预生成 Ghost 在发生变化前也会出现这种情况
                            if(!isPrespawn)
                                netDebug.LogWarning($"Trying to predict a ghost without having a state to roll back to {ghostSnapshotData.GetOldestTick(snapshotDataBuffer)} / {targetTick}");
                            // 这是完全没有可回滚状态的预测 Snapshot，如有可能则让它从上一个状态继续
                            var predictionStartTick = lastPredictedTick;
                            // 如果上一个 Tick 是部分 Tick，则尝试从备份恢复
                            if (predictionStateBackupTick.IsValid && TryGetChunkBackupState(chunk, ent, typeData.RollbackPredictionOnStructuralChanges,
                                    chunkEntities[ent], out var backupState, out var indexInBackup))
                            {
                                predictionStartTick = predictionStateBackupTick;
                                restoreFromBackupRange.Add(new BackupRange
                                {
                                    ent = ent,
                                    indexInBackup = indexInBackup,
                                    backupState = backupState
                                });
                            }
                            else if (!predictionStartTick.IsValid)
                            {
                                // 没有可供继续的上一状态，因此完全不运行预测
                                predictionStartTick = targetTick;
                            }
                            AddPredictionStartTick(targetTick, predictionStartTick);
                            var predictedData = PredictedGhostArray[ent];
                            predictedData.PredictionStartTick = predictionStartTick;
                            PredictedGhostArray[ent] = predictedData;
                        }
                    }
                }
                if (nextRange.y != 0)
                    entityRange.Add(nextRange);

                var requiredSendMask = predicted ? GhostSendType.OnlyPredictedClients : GhostSendType.OnlyInterpolatedClients;
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;

                // 此 Buffer 用于通过 MemCmp 比较变化，从而支持变更过滤
                var tempChangeBufferSize = 1_500;
                byte* tempChangeBuffer = stackalloc byte[tempChangeBufferSize];
                NativeArray<byte> tempChangeBufferLarge = default;

                if(restoreFromBackupRange.Length > 0)
                {
                    k_RestoreFromBackup.Begin();
                    RestorePredictionBackup(chunk, restoreFromBackupRange, typeData, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength);
                    k_RestoreFromBackup.End();
                }

                var enableableMaskOffset = 0;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (compIdx >= ghostChunkComponentTypesLength)
                        throw new System.InvalidOperationException("Component index out of range");
#endif
                    ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                    var snapshotSize = GhostComponentSerializer.SizeInSnapshot(ghostSerializer);
                    if (!chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]) || (GhostComponentIndex[typeData.FirstComponent + comp].SendMask&requiredSendMask) == 0)
                    {
                        snapshotDataOffset += snapshotSize;
                        if (typeData.EnableableBits > 0 && ghostSerializer.SerializesEnabledBit != 0)
                        {
                            ++enableableMaskOffset;
                            ValidateReadEnableBits(enableableMaskOffset, typeData.EnableableBits);
                        }
                        continue;
                    }

                    var componentHasChanges = false;
                    var compSize = ghostSerializer.ComponentSize;
                    if (!ghostSerializer.ComponentType.IsBuffer)
                    {
                        deserializerState.SendToOwner = ghostSerializer.SendToOwner;
                        if (ghostSerializer.HasGhostFields)
                        {
                            var roDynamicComponentTypeHandle = ghostChunkComponentTypesPtr[compIdx].CopyToReadOnly();
                            // 1. 从 Chunk 获取只读版本，后续始终通过这个稳定且不会变化的指针读写
                            var compDataPtr = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref roDynamicComponentTypeHandle, compSize).GetUnsafeReadOnlyPtr();
                            for (var rangeIdx = 0; rangeIdx < entityRange.Length; ++rangeIdx)
                            {
                                var range = entityRange[rangeIdx];
                                var snapshotData = (byte*)dataAtTick.GetUnsafeReadOnlyPtr();
                                snapshotData += snapshotDataAtTickSize * range.x;
                                // 快速路径：如果已经检测到变化，则直接获取读写版本并写入
                                if (componentHasChanges)
                                {
                                    var rwCompData = compDataPtr + range.x * compSize;
                                    ghostSerializer.CopyFromSnapshot.Invoke((System.IntPtr) UnsafeUtility.AddressOf(ref deserializerState), (System.IntPtr) snapshotData, snapshotDataOffset, snapshotDataAtTickSize, (System.IntPtr) rwCompData, compSize, range.y - range.x);
                                    continue;
                                }

                                var roCompData = compDataPtr + range.x * compSize;
                                // 2. 在区间循环内将其复制到足以容纳数据的临时 Buffer
                                var requiredNumBytes = (range.y - range.x) * compSize;
                                CopyRODataIntoTempChangeBuffer(requiredNumBytes, ref tempChangeBuffer, ref tempChangeBufferSize, ref tempChangeBufferLarge, roCompData);

                                // 3. 调用 CopyFromSnapshot，并把只读 Buffer 作为写入目标，这是一种绕过方式
                                ghostSerializer.CopyFromSnapshot.Invoke((System.IntPtr) UnsafeUtility.AddressOf(ref deserializerState), (System.IntPtr) snapshotData, snapshotDataOffset, snapshotDataAtTickSize, (System.IntPtr) roCompData, compSize, range.y - range.x);

                                // 4. 比较两个 Buffer 以检测变化
                                k_ChangeFiltering.Begin();
                                if (UnsafeUtility.MemCmp(roCompData, tempChangeBuffer, requiredNumBytes) != 0)
                                {
                                    // 5. 以读写方式获取数据以推进变更版本；数据已经写入，无需再次复制
                                    componentHasChanges = true;
                                    chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostChunkComponentTypesPtr[compIdx], compSize);
                                }
                                k_ChangeFiltering.End();
                            }
                            snapshotDataOffset += snapshotSize;
                        }
                    }
                    else
                    {
                        deserializerState.SendToOwner = ghostSerializer.SendToOwner;
                        if (ghostSerializer.HasGhostFields)
                        {
                            var roDynamicComponentTypeHandle = ghostChunkComponentTypesPtr[compIdx].CopyToReadOnly();
                            var bufferAccessor = chunk.GetUntypedBufferAccessor(ref roDynamicComponentTypeHandle);
                            var dynamicDataSize = ghostSerializer.SnapshotSize;
                            var maskBits = ghostSerializer.ChangeMaskBits;
                            for (var rangeIdx = 0; rangeIdx < entityRange.Length; ++rangeIdx)
                            {
                                var range = entityRange[rangeIdx];
                                for (int ent = range.x; ent < range.y; ++ent)
                                {
                                    // 为 Buffer 计算所需的 Owner Mask，并据此跳过 CopyFromSnapshot
                                    // 该检查必须对每个 Entity 分别执行
                                    if((ghostSerializer.SendToOwner & dataAtTick[ent].RequiredOwnerSendMask) == 0)
                                        continue;

                                    var dynamicDataBuffer = ghostSnapshotDynamicBufferArray[ent];
                                    var dynamicDataAtTick = SetupDynamicDataAtTick(dataAtTick[ent], snapshotDataOffset, dynamicDataSize, maskBits, dynamicDataBuffer, out var bufLen);
                                    var prevBufLen = bufferAccessor.GetBufferLength(ent);
                                    if(prevBufLen != bufLen)
                                    {
                                        if (!componentHasChanges)
                                        {
                                            componentHasChanges = true;
                                            // 推进变更版本
                                            bufferAccessor = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                        }
                                        bufferAccessor.ResizeUninitialized(ent, bufLen);
                                        var rwBufData = (byte*)bufferAccessor.GetUnsafePtr(ent);
                                        ghostSerializer.CopyFromSnapshot.Invoke(
                                            (System.IntPtr)UnsafeUtility.AddressOf(ref deserializerState),
                                            (System.IntPtr) UnsafeUtility.AddressOf(ref dynamicDataAtTick), 0, dynamicDataSize,
                                            (IntPtr)rwBufData, compSize, bufLen);
                                        continue;
                                    }

                                    var requiredNumBytes = bufLen * compSize;
                                    var roBufData = (byte*) bufferAccessor.GetUnsafeReadOnlyPtr(ent);
                                    CopyRODataIntoTempChangeBuffer(requiredNumBytes, ref tempChangeBuffer, ref tempChangeBufferSize, ref tempChangeBufferLarge, roBufData);

                                    // 同样通过绕过方式传入 roBufData 作为写入目标
                                    // 注意：根据上面的保证，这两个 Buffer 的大小必然完全相同
                                    ghostSerializer.CopyFromSnapshot.Invoke(
                                        (System.IntPtr)UnsafeUtility.AddressOf(ref deserializerState),
                                        (System.IntPtr) UnsafeUtility.AddressOf(ref dynamicDataAtTick), 0, dynamicDataSize,
                                        (IntPtr)roBufData, compSize, bufLen);

                                    k_ChangeFiltering.Begin();
                                    if (UnsafeUtility.MemCmp(roBufData, tempChangeBuffer, requiredNumBytes) != 0)
                                    {
                                        if (!componentHasChanges)
                                        {
                                            componentHasChanges = true;
                                            // 推进变更版本
                                            bufferAccessor = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                        };
                                    }
                                    k_ChangeFiltering.End();
                                }
                            }
                            snapshotDataOffset += snapshotSize;
                        }
                    }
                    if (typeData.EnableableBits > 0 && ghostSerializer.SerializesEnabledBit != 0)
                    {
                        for (var rangeIdx = 0; rangeIdx < entityRange.Length; ++rangeIdx)
                        {
                            var range = entityRange[rangeIdx];
                            // 以下逻辑会更新整个 Chunk 的启用位，因此数据应从区间起点获取
                            var dataAtTickPtr = (SnapshotData.DataAtTick*)dataAtTick.GetUnsafeReadOnlyPtr();
                            dataAtTickPtr += range.x;
                            UpdateEnableableMask(chunk, dataAtTickPtr, ghostSerializer.SendToOwner,
                                changeMaskUints, enableableMaskOffset, range, ghostChunkComponentTypesPtr, compIdx, ref componentHasChanges);
                        }
                        ++enableableMaskOffset;
                        ValidateReadEnableBits(enableableMaskOffset, typeData.EnableableBits);
                    }
                }
                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(ref linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException("Component index out of range");
#endif

                        ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                        var snapshotSize = GhostComponentSerializer.SizeInSnapshot(ghostSerializer);
                        if ((GhostComponentIndex[typeData.FirstComponent + comp].SendMask & requiredSendMask) == 0)
                        {
                            snapshotDataOffset += snapshotSize;
                            if (typeData.EnableableBits > 0 && ghostSerializer.SerializesEnabledBit != 0)
                            {
                                ++enableableMaskOffset;
                                ValidateReadEnableBits(enableableMaskOffset, typeData.EnableableBits);
                            }
                            continue;
                        }

                        var compSize = ghostSerializer.ComponentSize;
                        if (!ghostSerializer.ComponentType.IsBuffer)
                        {
                            deserializerState.SendToOwner = ghostSerializer.SendToOwner;
                            for (var rangeIdx = 0; rangeIdx < entityRange.Length; ++rangeIdx)
                            {
                                var range = entityRange[rangeIdx];
                                for (int ent = range.x; ent < range.y; ++ent)
                                {
                                    var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                    var childEntity = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                                    if (!childEntityLookup.Exists(childEntity))
                                        continue;
                                    var childChunk = childEntityLookup[childEntity];
                                    if (!childChunk.Chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                                        continue;

                                    // 仅出于性能原因通过 `GetUnsafeReadOnlyPtr` 获取这些数据，此处用法是安全的
                                    var dataAtTickPtr = (SnapshotData.DataAtTick*)dataAtTick.GetUnsafeReadOnlyPtr();
                                    dataAtTickPtr += ent;
                                    if (ghostSerializer.HasGhostFields)
                                    {
                                        // 此处没有快速路径
                                        // 1. 从 Chunk 获取只读版本
                                        var roDynamicComponentTypeHandle = ghostChunkComponentTypesPtr[compIdx].CopyToReadOnly();
                                        var roCompArray = childChunk.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref roDynamicComponentTypeHandle, compSize);
                                        var roCompData = (byte*) roCompArray.GetUnsafeReadOnlyPtr();
                                        roCompData += childChunk.IndexInChunk * compSize;

                                        // 2. 在区间循环内将其复制到足以容纳数据的临时 Buffer
                                        var requiredNumBytes = compSize;
                                        CopyRODataIntoTempChangeBuffer(requiredNumBytes, ref tempChangeBuffer, ref tempChangeBufferSize, ref tempChangeBufferLarge, roCompData);

                                        // 3. 调用 CopyFromSnapshot，并把只读 Buffer 作为写入目标，这是一种绕过方式
                                        ghostSerializer.CopyFromSnapshot.Invoke((System.IntPtr) UnsafeUtility.AddressOf(ref deserializerState), (System.IntPtr) dataAtTickPtr, snapshotDataOffset, snapshotDataAtTickSize, (System.IntPtr) roCompData, compSize, 1);

                                        // 4. 使用 MemCmp 比较两个 Buffer
                                        k_ChangeFiltering.Begin();
                                        if (UnsafeUtility.MemCmp(tempChangeBuffer, roCompData, compSize) != 0)
                                        {
                                            // 5. 如果存在变化，则获取读写版本并执行 MemCpy
                                            childChunk.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostChunkComponentTypesPtr[compIdx], compSize);
                                        }
                                        k_ChangeFiltering.End();
                                    }
                                    if (typeData.EnableableBits > 0 && ghostSerializer.SerializesEnabledBit != 0)
                                    {
                                        var childRange = new int2 { x = childChunk.IndexInChunk, y = childChunk.IndexInChunk + 1 };
                                        var unused = false;
                                        UpdateEnableableMask(childChunk.Chunk, dataAtTickPtr, ghostSerializer.SendToOwner,
                                            changeMaskUints, enableableMaskOffset, childRange, ghostChunkComponentTypesPtr, compIdx, ref unused);
                                    }
                                }
                            }
                            if (typeData.EnableableBits > 0 && ghostSerializer.SerializesEnabledBit != 0)
                            {
                                ++enableableMaskOffset;
                                ValidateReadEnableBits(enableableMaskOffset, typeData.EnableableBits);
                            }
                            snapshotDataOffset += snapshotSize;
                        }
                        else // Component 类型是 Buffer
                        {
                            var dynamicDataSize = ghostSerializer.SnapshotSize;
                            var maskBits = ghostSerializer.ChangeMaskBits;
                            deserializerState.SendToOwner = ghostSerializer.SendToOwner;
                            for (var rangeIdx = 0; rangeIdx < entityRange.Length; ++rangeIdx)
                            {
                                var range = entityRange[rangeIdx];
                                var maskOffset = enableableMaskOffset;
                                for (int rootEntity = range.x; rootEntity < range.y; ++rootEntity)
                                {
                                    var linkedEntityGroup = linkedEntityGroupAccessor[rootEntity];
                                    var childEntity = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                                    if (!childEntityLookup.Exists(childEntity))
                                        continue;
                                    var childChunk = childEntityLookup[childEntity];
                                    if (!childChunk.Chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                                        continue;

                                    if((ghostSerializer.SendToOwner & dataAtTick[rootEntity].RequiredOwnerSendMask) == 0)
                                        continue;

                                    if (ghostSerializer.HasGhostFields)
                                    {
                                        var roDynamicComponentTypeHandle = ghostChunkComponentTypesPtr[compIdx].CopyToReadOnly();
                                        var roBufferAccessor = childChunk.Chunk.GetUntypedBufferAccessor(ref roDynamicComponentTypeHandle);

                                        var dynamicDataBuffer = ghostSnapshotDynamicBufferArray[rootEntity];
                                        var dynamicDataAtTick = SetupDynamicDataAtTick(dataAtTick[rootEntity], snapshotDataOffset, dynamicDataSize, maskBits, dynamicDataBuffer, out var bufLen);
                                        var prevBufLen = roBufferAccessor.GetBufferLength(childChunk.IndexInChunk);
                                        if (prevBufLen != bufLen)
                                        {
                                            var rwBufferAccessor = childChunk.Chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                            rwBufferAccessor.ResizeUninitialized(childChunk.IndexInChunk, bufLen);
                                            var rwBufData = rwBufferAccessor.GetUnsafePtr(childChunk.IndexInChunk);

                                            ghostSerializer.CopyFromSnapshot.Invoke(
                                                (System.IntPtr) UnsafeUtility.AddressOf(ref deserializerState),
                                                (System.IntPtr) UnsafeUtility.AddressOf(ref dynamicDataAtTick), 0, dynamicDataSize,
                                                (IntPtr) rwBufData, compSize, bufLen);
                                        }
                                        else
                                        {
                                            var roBufData = (byte*) roBufferAccessor.GetUnsafeReadOnlyPtr(childChunk.IndexInChunk);
                                            var requiredNumBytes = bufLen * compSize;
                                            CopyRODataIntoTempChangeBuffer(requiredNumBytes, ref tempChangeBuffer, ref tempChangeBufferSize, ref tempChangeBufferLarge, roBufData);

                                            // 同样通过绕过方式传入 roBufData 作为写入目标
                                            // 注意：根据上面的保证，这两个 Buffer 的大小必然完全相同
                                            ghostSerializer.CopyFromSnapshot.Invoke(
                                                (System.IntPtr) UnsafeUtility.AddressOf(ref deserializerState),
                                                (System.IntPtr) UnsafeUtility.AddressOf(ref dynamicDataAtTick), 0, dynamicDataSize,
                                                (IntPtr) roBufData, compSize, bufLen);

                                            k_ChangeFiltering.Begin();

                                            if (UnsafeUtility.MemCmp(roBufData, tempChangeBuffer, requiredNumBytes) != 0)
                                            {
                                                // 推进变更版本
                                                childChunk.Chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                            }
                                            k_ChangeFiltering.End();
                                        }
                                    }

                                    if (typeData.EnableableBits > 0 && ghostSerializer.SerializesEnabledBit != 0)
                                    {
                                        var snapshotData = (byte*) dataAtTick.GetUnsafeReadOnlyPtr();
                                        snapshotData += snapshotDataAtTickSize * rootEntity;
                                        var dataAtTickPtr = (SnapshotData.DataAtTick*) snapshotData;

                                        var childRange = new int2 {x = childChunk.IndexInChunk, y = childChunk.IndexInChunk + 1};
                                        var unused = false;
                                        UpdateEnableableMask(childChunk.Chunk, dataAtTickPtr,
                                            ghostSerializer.SendToOwner,
                                            changeMaskUints, maskOffset, childRange, ghostChunkComponentTypesPtr, compIdx, ref unused);
                                    }
                                }
                            }
                            if (typeData.EnableableBits > 0 && ghostSerializer.SerializesEnabledBit != 0)
                            {
                                ++enableableMaskOffset;
                                ValidateReadEnableBits(enableableMaskOffset, typeData.EnableableBits);
                            }
                            snapshotDataOffset += snapshotSize;
                        }
                    }
                }
                ValidateAllEnableBitsHasBeenRead(enableableMaskOffset, typeData.EnableableBits);
            }

            private bool TryGetChunkBackupState(in ArchetypeChunk chunk, int indexInChunk, int rollbackOnStructuralChanges,
                Entity entity, out IntPtr backupState, out int remappedIndex)
            {
                using var _ = k_TryGetChunkBackupState.Auto();
                backupState = IntPtr.Zero;
                remappedIndex = -1;
                // 首先检查 Entity 是否存在于上一次备份中；如果不存在便无法恢复
                if (!predictionBackupEntityState.TryGetValue(entity, out var lastState))
                    return false;

                // 备份保存了给定 Chunk 的稳定信息，因此始终依赖 LastIndexInChunk 从正确索引恢复
                // 但如果 Archetype 保留旧行为，则不查找缓存值，而是使用当前 Chunk 和索引
                if (rollbackOnStructuralChanges == 1)
                {
                    if (!predictionStateBackup.TryGetValue(chunk, out backupState))
                        return false;
                    remappedIndex = indexInChunk;
                    return PredictionBackupState.MatchEntity(backupState, indexInChunk, entity);
                }
                // 如果与上次使用的备份 Chunk 相同，则只需检查指针即可获取它
                if (!predictionStateBackup.TryGetValue(lastState.lastChunk, out backupState))
                    return false;
                remappedIndex = lastState.LastIndexInChunk;
                // 即使结构变更导致备份时的 Chunk 与当前 Chunk 不同，也可以使用备份时保存的 Entity 原始信息
                // 找到备份条目，并相应重映射索引以访问备份数据
                return PredictionBackupState.MatchEntity(backupState, lastState.LastIndexInChunk, entity);
            }

            private static void CopyRODataIntoTempChangeBuffer(int requiredCompDataLength, ref byte* tempChangeBuffer, ref int tempChangeBufferSize, ref NativeArray<byte> tempChangeBufferLarge, byte* roCompData)
            {
                k_ChangeFiltering.Begin();
                if (requiredCompDataLength > tempChangeBufferSize)
                {
                    tempChangeBufferLarge = new NativeArray<byte>(math.ceilpow2(requiredCompDataLength), Allocator.Temp);
                    tempChangeBuffer = (byte*) tempChangeBufferLarge.GetUnsafePtr();
                    tempChangeBufferSize = tempChangeBufferLarge.Length;
                }
                UnsafeUtility.MemCpy(tempChangeBuffer, roCompData, requiredCompDataLength);
                k_ChangeFiltering.End();
            }

            // TODO: 可以使用 EnabledMask 更快地执行此逻辑
            private static void UpdateEnableableMask(ArchetypeChunk chunk, SnapshotData.DataAtTick* dataAtTickPtr,
                SendToOwnerType ownerSendMask,
                int changeMaskUints, int enableableMaskOffset, int2 range,
                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int compIdx, ref bool componentHasChanges)
            {
                var uintOffset = enableableMaskOffset >> 5;
                var maskOffset = enableableMaskOffset & 0x1f;
                for (int i = range.x; i < range.y; ++i, ++dataAtTickPtr)
                {
                    var snapshotDataPtr = (byte*)dataAtTickPtr->SnapshotBefore;
                    uint* enableableMasks = (uint*)(snapshotDataPtr + sizeof(uint) + changeMaskUints * sizeof(uint));
                    enableableMasks += uintOffset;
                    if ((dataAtTickPtr->RequiredOwnerSendMask & ownerSendMask) == 0)
                        continue;
                    var isSet = ((*enableableMasks) & (1U << maskOffset)) != 0;
                    k_ChangeFiltering.Begin();
                    if (isSet != chunk.IsComponentEnabled(ref ghostChunkComponentTypesPtr[compIdx], i))
                    {
                        componentHasChanges = true;
                        k_ChangeFiltering.End();
                        chunk.SetComponentEnabled(ref ghostChunkComponentTypesPtr[compIdx], i, isSet);
                    }

                    else k_ChangeFiltering.End();
                }
            }

            static SnapshotData.DataAtTick SetupDynamicDataAtTick(in SnapshotData.DataAtTick dataAtTick,
                int snapshotOffset, int snapshotSize, int maskBits, in DynamicBuffer<SnapshotDynamicDataBuffer> ghostSnapshotDynamicBuffer, out int buffernLen)
            {
                // 从 Snapshot 中获取 Buffer 信息
                var snapshotData = (int*)(dataAtTick.SnapshotBefore + snapshotOffset);
                var bufLen = snapshotData[0];
                var dynamicDataOffset = snapshotData[1];
                // 动态 Snapshot 数据关联到根 Entity，而不是子 Entity
                var dynamicSnapshotDataBeforePtr = SnapshotDynamicBuffersHelper.GetDynamicDataPtr((byte*)ghostSnapshotDynamicBuffer.GetUnsafeReadOnlyPtr(),
                    dataAtTick.BeforeIdx, ghostSnapshotDynamicBuffer.Length);
                //var dynamicSnapshotDataCapacity = SnapshotDynamicBuffersHelper.GetDynamicDataCapacity(SnapshotDynamicBuffersHelper.GetHeaderSize(),ghostSnapshotDynamicBuffer.Length);
                var dynamicMaskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(maskBits, bufLen);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if ((dynamicDataOffset + bufLen*snapshotSize) > ghostSnapshotDynamicBuffer.Length)
                    throw new System.InvalidOperationException("Overflow reading data from dynamic snapshot memory buffer");
#endif
                // 将 Snapshot 数据复制到 Buffer，并使用临时 DataAtTick 向 Serializer 函数传递信息
                // 无需为每个元素分别使用一个 DataAtTick，那会造成不必要的开销
                buffernLen = bufLen;
                return new SnapshotData.DataAtTick
                {
                    SnapshotBefore = (System.IntPtr)(dynamicSnapshotDataBeforePtr + dynamicDataOffset + dynamicMaskSize),
                    SnapshotAfter = (System.IntPtr)(dynamicSnapshotDataBeforePtr + dynamicDataOffset + dynamicMaskSize),
                    // 此处不需要插值系数
                    InterpolationFactor = 0.0f,
                    Tick = dataAtTick.Tick
                };
            }

            struct RestoreState
            {
                public byte* dataPtr;
                public ulong* enableBits;
                public byte* bufferBackupDataPtr;
                public uint* chunkVersionPtr;
                public uint* childChunkVersionPtr;
            }

            void RestorePredictionBackup(ArchetypeChunk chunk,
                NativeList<BackupRange> toRestore,
                in GhostCollectionPrefabSerializer typeData,
                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr,
                int ghostChunkComponentTypesLength)
            {
                // 调用此方法时，toRestore 的长度必须大于 0
                Assertions.Assert.IsTrue(toRestore.Length > 0);

                int baseOffset = typeData.FirstComponent;
                const GhostSendType requiredSendMask = GhostSendType.OnlyPredictedClients;
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                Span<RestoreState> allStates = stackalloc RestoreState[toRestore.Length];
                Span<int> toUpdateIdx = stackalloc int[toRestore.Length];
                for (int i = 0; i < toRestore.Length; ++i)
                {
                    allStates[i].dataPtr = PredictionBackupState.GetData(toRestore[i].backupState);
                    allStates[i].enableBits = PredictionBackupState.GetEnabledBits(toRestore[i].backupState);
                    allStates[i].bufferBackupDataPtr = PredictionBackupState.GetBufferDataPtr(toRestore[i].backupState);
                    allStates[i].chunkVersionPtr = PredictionBackupState.GetChunkVersion(toRestore[i].backupState);
                    allStates[i].childChunkVersionPtr = allStates[i].chunkVersionPtr + numBaseComponents;
                    toUpdateIdx[i] = -1; // 避免意外使用未初始化的索引
                }

                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[baseOffset + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (compIdx >= ghostChunkComponentTypesLength)
                        throw new System.InvalidOperationException("Component index out of range");
#endif
                    // 数据不存在于备份 Buffer 中，参见 GhostPredictionHistorySystem.cs 中的规则
                    if ((GhostComponentIndex[baseOffset + comp].SendMask&requiredSendMask) == 0)
                        continue;

                    ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                    var compSize = ghostSerializer.ComponentType.IsBuffer
                        ? GhostComponentSerializer.DynamicBufferComponentSnapshotSize
                        : ghostSerializer.ComponentSize;
                    if (!ghostSerializer.HasGhostFields)
                        compSize = 0;

                    if (!chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                    {
                        for (var entIndex = 0; entIndex < toRestore.Length; entIndex++)
                        {
                            if (ghostSerializer.HasGhostFields)
                                allStates[entIndex].dataPtr = PredictionBackupState.GetNextData(allStates[entIndex].dataPtr, compSize, PredictionBackupState.GetEntityCapacity(toRestore[entIndex].backupState));
                            if(ghostSerializer.SerializesEnabledBit != 0)
                                allStates[entIndex].enableBits = PredictionBackupState.GetNextEnabledBits(allStates[entIndex].enableBits, PredictionBackupState.GetEntityCapacity(toRestore[entIndex].backupState));
                        }
                        continue;
                    }

                    int toUpdateCount = 0;
                    for (var index = 0; index < toRestore.Length; index++)
                    {
                        // 从备份恢复时只需检查 Chunk 版本；只要有逻辑访问并修改过该 Component，它就应被视为已变更
                        // 不应通过补偿来改变这一语义
                        uint backupVersion = allStates[index].chunkVersionPtr[comp];
                        k_ChangeFiltering.Begin();
                        // 现在会为 Entity 复制并重映射 Chunk 状态，因此不能再因为 Chunk 中部分 Entity 的状态
                        // 属于旧 Chunk 就跳过整个恢复过程；Entity 发生移动后，这些版本基本都会失效
                        if (chunk.DidChange(ref ghostChunkComponentTypesPtr[compIdx], backupVersion))
                        {
                            toUpdateIdx[toUpdateCount] = index;
                            ++toUpdateCount;
                        }
                        else
                        {
                            if(ghostSerializer.HasGhostFields)
                                allStates[index].dataPtr = PredictionBackupState.GetNextData(allStates[index].dataPtr, compSize, PredictionBackupState.GetEntityCapacity(toRestore[index].backupState));
                            if(ghostSerializer.SerializesEnabledBit != 0)
                                allStates[index].enableBits = PredictionBackupState.GetNextEnabledBits(allStates[index].enableBits, PredictionBackupState.GetEntityCapacity(toRestore[index].backupState));
                        }
                        k_ChangeFiltering.End();
                    }

                    if(toUpdateCount == 0)
                        continue;

                    if (ghostSerializer.SerializesEnabledBit != 0)
                    {
                        for (var idx = 0; idx < toUpdateCount; idx++)
                        {
                            var toRestoreIdx = toUpdateIdx[idx];
                            var indexInBackup = toRestore[toRestoreIdx].indexInBackup;
                            var requiredOwnerMask = GetRequiredOwnerMask(toRestore[toRestoreIdx].backupState, indexInBackup);
                            // 如果客户端根据 PlayerGhostFilter 设置永远不会接收该 Component，则不从备份恢复
                            // 该 Component 仍存在于 Buffer 中，因此需要跳过对应数据
                            if ((ghostSerializer.SendToOwner & requiredOwnerMask) != 0)
                            {
                                bool isSet = (allStates[toRestoreIdx].enableBits[indexInBackup >> 6] & (1ul << (indexInBackup & 0x3f))) != 0;
                                chunk.SetComponentEnabled(ref ghostChunkComponentTypesPtr[compIdx], toRestore[toRestoreIdx].ent, isSet);
                            }
                            allStates[toRestoreIdx].enableBits = PredictionBackupState.GetNextEnabledBits(allStates[toRestoreIdx].enableBits, PredictionBackupState.GetEntityCapacity(toRestore[toRestoreIdx].backupState));
                        }
                    }
                    // 如果 Component 没有任何 Ghost Field，则没有数据需要恢复，也无需推进数据指针
                    // 备份 Buffer 没有为该 Component 预留空间，参见 GhostPredictionHistorySystem
                    if (!ghostSerializer.HasGhostFields)
                        continue;

                    if (!ghostSerializer.ComponentType.IsBuffer)
                    {
                        var compData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafePtr();
                        // TODO: 批量调用从备份恢复的函数
                        for (var idx = 0; idx < toUpdateCount; idx++)
                        {
                            var toRestoreIdx = toUpdateIdx[idx];
                            var indexInBackup = toRestore[toRestoreIdx].indexInBackup;
                            var requiredOwnerMask = GetRequiredOwnerMask(toRestore[toRestoreIdx].backupState, indexInBackup);
                            // 如果客户端根据 PlayerGhostFilter 设置永远不会接收该 Component，则不从备份恢复
                            // 该 Component 仍存在于 Buffer 中，因此需要跳过对应数据
                            if ((ghostSerializer.SendToOwner & requiredOwnerMask) != 0)
                            {
                                ghostSerializer.RestoreFromBackup.Invoke((System.IntPtr)(compData + toRestore[toRestoreIdx].ent * compSize),
                                    (System.IntPtr)(allStates[toRestoreIdx].dataPtr + indexInBackup * compSize));
                            }
                            allStates[toRestoreIdx].dataPtr = PredictionBackupState.GetNextData(allStates[toRestoreIdx].dataPtr, compSize,
                                PredictionBackupState.GetEntityCapacity(toRestore[toRestoreIdx].backupState));
                        }
                    }
                    else
                    {
                        var bufferAccessor = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                        for (var idx = 0; idx < toUpdateCount; idx++)
                        {
                            var toRestoreIdx = toUpdateIdx[idx];
                            var indexInBackup = toRestore[toRestoreIdx].indexInBackup;
                            var backupData = (int*)(allStates[toRestoreIdx].dataPtr + indexInBackup * compSize);
                            var bufLen = backupData[0];
                            var bufOffset = backupData[1];
                            var elemSize = ghostSerializer.ComponentSize;
                            var bufferDataPtr = allStates[toRestoreIdx].bufferBackupDataPtr + bufOffset;

                            // 如果客户端永远不会接收该 Component，则不从备份恢复
                            var requiredOwnerMask = GetRequiredOwnerMask(toRestore[toRestoreIdx].backupState, indexInBackup);
                            if ((ghostSerializer.SendToOwner & requiredOwnerMask) != 0)
                            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                if ((bufOffset + bufLen * elemSize) > PredictionBackupState.GetBufferDataCapacity(toRestore[toRestoreIdx].backupState))
                                    throw new System.InvalidOperationException("Overflow reading data from dynamic snapshot memory buffer");
#endif
                                // 重要：RestoreFromBackup 只恢复给定结构体中已序列化的字段
                                // 与 Component 不同，动态 Snapshot Buffer 调整大小时出于性能考虑不会清空内存
                                // 如果某些元素字段未标记 [GhostField]，部分数据可能保持未初始化并含有随机值
                                // 因此强制要求 BufferElementData 的所有字段都标记 GhostFieldAttribute
                                // 该限制解决了当前问题，之后可能会放宽
                                bufferAccessor.ResizeUninitialized(toRestore[toRestoreIdx].ent, bufLen);
                                var bufferPointer = (byte*)bufferAccessor.GetUnsafePtr(toRestore[toRestoreIdx].ent);
                                // 对 Buffer 或许可以直接使用 memcpy，因为规则要求所有字段都有 [GhostField]，所以全部数据都会复制
                                // 但内部字段或属性不会被复制，代码生成也不会因其存在而报错
                                // 由于 Buffer 初始化时这些成员可能含有随机内存值，它们仍会造成问题，通常应避免使用
                                // 因此 memcpy 可能是更快且更正确的路径，但也会引入带有倾向性的限制并改变现有行为
                                // 这种变化或许适合 2.0，但当前 1.x 应避免破坏用户行为，尽管它很可能不会影响实际项目
                                // TODO: 批量处理此逻辑
                                for (int bufElement = 0; bufElement < bufLen; ++bufElement)
                                {
                                    ghostSerializer.RestoreFromBackup.Invoke((System.IntPtr)(bufferPointer), (System.IntPtr)(bufferDataPtr));
                                    bufferPointer += elemSize;
                                    bufferDataPtr += elemSize;
                                }
                            }
                            allStates[toRestoreIdx].dataPtr = PredictionBackupState.GetNextData(allStates[toRestoreIdx].dataPtr, compSize,
                                PredictionBackupState.GetEntityCapacity(toRestore[toRestoreIdx].backupState));
                        }
                    }
                }
                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(ref linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[baseOffset + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException("Component index out of range");
#endif
                        // 不存在于备份 Buffer 中，参见 GhostPredictionHistorySystem.cs 中的规则
                        if ((GhostComponentIndex[baseOffset + comp].SendMask & requiredSendMask) == 0)
                            continue;
                        ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                        var compSize = ghostSerializer.ComponentType.IsBuffer
                            ? GhostComponentSerializer.DynamicBufferComponentSnapshotSize
                            : ghostSerializer.ComponentSize;
                        if (!ghostSerializer.HasGhostFields)
                            compSize = 0;

                        var readonlyHandle = ghostChunkComponentTypesPtr[compIdx].CopyToReadOnly();
                        var childIndex = GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex;
                        for (var toRestoreIdx = 0; toRestoreIdx < toRestore.Length; toRestoreIdx++)
                        {
                            var rootEnt = toRestore[toRestoreIdx].ent;
                            var linkedEntityGroup = linkedEntityGroupAccessor[rootEnt];
                            var childEnt = linkedEntityGroup[childIndex].Value;

                            if (!childEntityLookup.TryGetValue(childEnt, out var childChunk) || !childChunk.Chunk.Has(ref readonlyHandle))
                                continue;
                            var indexInBackup = toRestore[toRestoreIdx].indexInBackup;
                            uint backupVersion = allStates[toRestoreIdx].childChunkVersionPtr[indexInBackup];
                            k_ChangeFiltering.Begin();
                            if (!childChunk.Chunk.DidChange(ref readonlyHandle, backupVersion))
                            {
                                k_ChangeFiltering.End();
                                continue;
                            }
                            else k_ChangeFiltering.End();
                            // Owner 仍然是 rootEnt，而不是子 Entity
                            var requiredOwnerMask = GetRequiredOwnerMask(toRestore[toRestoreIdx].backupState, indexInBackup);
                            if ((ghostSerializer.SendToOwner & requiredOwnerMask) != 0)
                            {
                                if (ghostSerializer.SerializesEnabledBit != 0)
                                {
                                    bool isSet = (allStates[toRestoreIdx].enableBits[indexInBackup >> 6] & (1ul << (indexInBackup & 0x3f))) != 0;
                                    childChunk.Chunk.SetComponentEnabled(ref ghostChunkComponentTypesPtr[compIdx], childChunk.IndexInChunk, isSet);
                                }

                                // 如果 Component 没有任何 Ghost Field，则没有数据需要恢复，也无需推进数据指针
                                // 备份 Buffer 没有为该 Component 预留空间，参见 GhostPredictionHistorySystem
                                if (!ghostSerializer.HasGhostFields)
                                    continue;

                                if (!ghostSerializer.ComponentType.IsBuffer)
                                {
                                    var compData = (byte*)childChunk.Chunk
                                        .GetDynamicComponentDataArrayReinterpret<byte>(ref readonlyHandle, compSize)
                                        .GetUnsafeReadOnlyPtr();
                                    ghostSerializer.RestoreFromBackup.Invoke(
                                        (System.IntPtr)(compData + childChunk.IndexInChunk * compSize),
                                        (System.IntPtr)(allStates[toRestoreIdx].dataPtr + indexInBackup * compSize));
                                }
                                else
                                {
                                    var backupData = (int*)(allStates[toRestoreIdx].dataPtr + indexInBackup * compSize);
                                    var bufLen = backupData[0];
                                    var bufOffset = backupData[1];
                                    var elemSize = ghostSerializer.ComponentSize;
                                    var bufferDataPtr = allStates[toRestoreIdx].bufferBackupDataPtr + bufOffset;
    #if ENABLE_UNITY_COLLECTIONS_CHECKS
                                    if ((bufOffset + bufLen * elemSize) > PredictionBackupState.GetBufferDataCapacity(toRestore[toRestoreIdx].backupState))
                                        throw new System.InvalidOperationException("Overflow reading data from dynamic snapshot memory buffer");
    #endif
                                    var bufferAccessor = childChunk.Chunk.GetUntypedBufferAccessor(ref readonlyHandle);
                                    bufferAccessor.ResizeUninitialized(childChunk.IndexInChunk, bufLen);
                                    var bufferPointer = (byte*)bufferAccessor.GetUnsafePtr(childChunk.IndexInChunk);
                                    for (int bulElement = 0; bulElement < bufLen; ++bulElement)
                                    {
                                        ghostSerializer.RestoreFromBackup.Invoke((System.IntPtr)(bufferPointer), (System.IntPtr)(bufferDataPtr));
                                        bufferPointer += elemSize;
                                        bufferDataPtr += elemSize;
                                    }
                                }
                            }
                        }
                        // 备份中的数据按 Component 分组存储，布局如下：
                        // C1       | C2       | ChildComp1    | ChildComp2
                        // e1,e2,e3 | e1,e2,e3 | e1c1,e2c1,e3c1| ...
                        // 因此必须在这里推进数据指针、启用位和 Chunk 版本，而不是每恢复一个 Entity 就推进一次
                        for (var entIndex = 0; entIndex < toRestore.Length; entIndex++)
                        {
                            if (ghostSerializer.SerializesEnabledBit != 0)
                                allStates[entIndex].enableBits = PredictionBackupState.GetNextEnabledBits(allStates[entIndex].enableBits,
                                    PredictionBackupState.GetEntityCapacity(toRestore[entIndex].backupState));
                            if (ghostSerializer.HasGhostFields)
                            {
                                allStates[entIndex].dataPtr = PredictionBackupState.GetNextData(allStates[entIndex].dataPtr, compSize,
                                    PredictionBackupState.GetEntityCapacity(toRestore[entIndex].backupState));
                            }
                            if (ghostSerializer.HasGhostFields || ghostSerializer.SerializesEnabledBit != 0)
                            {
                                allStates[entIndex].childChunkVersionPtr = PredictionBackupState.GetNextChildChunkVersion(allStates[entIndex].childChunkVersionPtr,
                                    PredictionBackupState.GetEntityCapacity(toRestore[entIndex].backupState));
                            }
                        }
                    }
                }
            }

            private SendToOwnerType GetRequiredOwnerMask(IntPtr state, int ent)
            {
                var ghostOwner = PredictionBackupState.GetGhostOwner(state, ent);
                var requiredOwnerMask = SendToOwnerType.All;
                if (ghostOwnerId != 0 && ghostOwner >= 0)
                {
                    requiredOwnerMask = ghostOwnerId == ghostOwner
                        ? SendToOwnerType.SendToOwner
                        : SendToOwnerType.SendToNonOwner;
                }

                return requiredOwnerMask;
            }
        }

        [BurstCompile]
        struct CalculateNumPredictedGhostToRollback : IJobChunk
        {
            [ReadOnly]public ComponentTypeHandle<PredictedGhost> predictedGhostTypeHandle;
            [ReadOnly]public ComponentTypeHandle<SnapshotData> ghostSnapshotDataType;
            [ReadOnly]public BufferTypeHandle<SnapshotDataBuffer> ghostSnapshotDataBufferType;
            [NativeDisableParallelForRestriction]
            public NativeArray<int> numPredictedGhostWithNewData;
            [NativeSetThreadIndex] public int threadIndex;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // 这样可以避免伪共享：每个整数分配在不同的缓存行中，写入某个槽位不会触发 CPU 缓存同步
                // 除以 sizeof(int) 是为了把缓存行字节数转换为整数槽位数
                int index = threadIndex * JobsUtility.CacheLineSize / sizeof(int);
                var predictedGhosts = chunk.GetComponentDataPtrRO(ref predictedGhostTypeHandle);
                var ghostSnapshotDataArray = chunk.GetNativeArray(ref ghostSnapshotDataType);
                var ghostSnapshotDataBufferArray = chunk.GetBufferAccessor(ref ghostSnapshotDataBufferType);
                for (int i = 0; i < chunk.Count; ++i)
                {
                    var snapshotData = ghostSnapshotDataArray[i];
                    var latestTick = snapshotData.GetLatestTick(ghostSnapshotDataBufferArray[i]);
                    var lastAppliedTick = predictedGhosts[i].AppliedTick;
                    if (latestTick.IsValid && (!lastAppliedTick.IsValid || latestTick.IsNewerThan(lastAppliedTick)))
                    {
                        ++numPredictedGhostWithNewData[index];
                    }
                }
            }
        }


        [BurstCompile]
        struct UpdateLastInterpolatedTick : IJob
        {
            [ReadOnly]
            public ComponentLookup<NetworkSnapshotAck> AckFromEntity;
            public Entity                                               AckSingleton;
            public NativeReference<NetworkTick>                         LastInterpolatedTick;
            public NetworkTick                                          InterpolationTick;
            public float                                                InterpolationTickFraction;

            public void Execute()
            {
                var ack = AckFromEntity[AckSingleton];
                if (InterpolationTick.IsValid && ack.LastReceivedSnapshotByLocal.IsValid && !InterpolationTick.IsNewerThan(ack.LastReceivedSnapshotByLocal))
                {
                    var lastInterpolTick = InterpolationTick;
                    // 确保记录的是最后一个完整插值 Tick，此值只用于判断静态 Ghost 是否已经应用最新状态
                    if (InterpolationTickFraction < 1)
                        lastInterpolTick.Decrement();
                    LastInterpolatedTick.Value = lastInterpolTick;
                }
            }
        }

        static readonly Unity.Profiling.ProfilerMarker k_Scheduling = new Unity.Profiling.ProfilerMarker("GhostUpdateSystem_Scheduling");
        static readonly Unity.Profiling.ProfilerMarker k_ChangeFiltering = new Unity.Profiling.ProfilerMarker("GhostUpdateSystem_ChangeFiltering");
        static readonly Unity.Profiling.ProfilerMarker k_RestoreFromBackup = new Unity.Profiling.ProfilerMarker("GhostUpdateSystem_RestoreFromBackup");
        static readonly Unity.Profiling.ProfilerMarker k_TryGetChunkBackupState = new Unity.Profiling.ProfilerMarker("GhostUpdateSystem_TryGetChunkBackupState");
        private EntityQuery m_ghostQuery;
        private EntityQuery m_PredictedGhostQuery;
        private NetworkTick m_LastPredictedTick;
        private NativeReference<NetworkTick> m_LastInterpolatedTick;
        private NativeParallelHashMap<NetworkTick, NetworkTick> m_AppliedPredictedTicks;
        private NativeArray<int> m_NumPredictedGhostWithNewData;

        BufferLookup<GhostComponentSerializer.State> m_GhostComponentCollectionFromEntity;
        BufferLookup<GhostCollectionPrefabSerializer> m_GhostTypeCollectionFromEntity;
        BufferLookup<GhostCollectionComponentIndex> m_GhostComponentIndexFromEntity;
        ComponentLookup<NetworkSnapshotAck> m_NetworkSnapshotAckLookup;

        ComponentTypeHandle<PredictedGhost> m_PredictedGhostTypeHandle;
        ComponentTypeHandle<GhostInstance> m_GhostComponentTypeHandle;
        ComponentTypeHandle<GhostType> m_GhostTypeHandle;
        ComponentTypeHandle<SnapshotData> m_SnapshotDataTypeHandle;
        BufferTypeHandle<SnapshotDataBuffer> m_SnapshotDataBufferTypeHandle;
        BufferTypeHandle<SnapshotDynamicDataBuffer> m_SnapshotDynamicDataBufferTypeHandle;
        BufferTypeHandle<LinkedEntityGroup> m_LinkedEntityGroupTypeHandle;
        ComponentTypeHandle<PreSpawnedGhostIndex> m_PreSpawnedGhostIndexTypeHandle;
        ComponentTypeHandle<PredictedGhostSpawnRequest> m_PredictedGhostSpawnRequestTypeHandle;
        EntityTypeHandle m_EntityTypeHandle;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState systemState)
        {
            if (systemState.WorldUnmanaged.IsHost())
            {
                systemState.Enabled = false;
                return;
            }

#if UNITY_2022_2_14F1_OR_NEWER
            int maxThreadCount = JobsUtility.ThreadIndexCount;
#else
            int maxThreadCount = JobsUtility.MaxJobThreadCount;
#endif

            var ghostUpdateVersionSingleton = systemState.EntityManager.CreateEntity(ComponentType.ReadWrite<GhostUpdateVersion>());
            systemState.EntityManager.SetName(ghostUpdateVersionSingleton, "GhostUpdateVersion-Singleton");

            m_AppliedPredictedTicks = new NativeParallelHashMap<NetworkTick, NetworkTick>(CommandDataUtility.k_CommandDataMaxSize*maxThreadCount / 4, Allocator.Persistent);
            var singletonEntity = systemState.EntityManager.CreateEntity(ComponentType.ReadWrite<GhostPredictionGroupTickState>());
            systemState.EntityManager.SetName(singletonEntity, "AppliedPredictedTicks-Singleton");
            SystemAPI.SetSingleton(new GhostPredictionGroupTickState { AppliedPredictedTicks = m_AppliedPredictedTicks });

            var queryBuilder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SnapshotData, GhostInstance>()
                .WithAllRW<SnapshotDataBuffer>()
                .WithAbsent<PendingSpawnPlaceholder>();
            m_ghostQuery = queryBuilder.Build(systemState.EntityManager);
            queryBuilder.Reset();
            queryBuilder.WithAll<PredictedGhost, SnapshotData, SnapshotDataBuffer>()
                .WithNone<PendingSpawnPlaceholder>();
            m_PredictedGhostQuery = queryBuilder.Build(systemState.EntityManager);
            systemState.RequireForUpdate<NetworkStreamInGame>();
            systemState.RequireForUpdate<GhostCollection>();

            m_LastInterpolatedTick = new NativeReference<NetworkTick>(Allocator.Persistent);
            // 为每个工作线程的每条缓存行分配一个整数
            // 每条缓存行最多包含 CacheLineSize / sizeof(int) 个整数，因此容量需要除以 sizeof(int)
            m_NumPredictedGhostWithNewData = new NativeArray<int>(JobsUtility.ThreadIndexCount * JobsUtility.CacheLineSize / sizeof(int), Allocator.Persistent);
            m_GhostComponentCollectionFromEntity = systemState.GetBufferLookup<GhostComponentSerializer.State>(true);
            m_GhostTypeCollectionFromEntity = systemState.GetBufferLookup<GhostCollectionPrefabSerializer>(true);
            m_GhostComponentIndexFromEntity = systemState.GetBufferLookup<GhostCollectionComponentIndex>(true);
            m_NetworkSnapshotAckLookup = systemState.GetComponentLookup<NetworkSnapshotAck>(true);
            m_PredictedGhostTypeHandle = systemState.GetComponentTypeHandle<PredictedGhost>();
            m_GhostComponentTypeHandle = systemState.GetComponentTypeHandle<GhostInstance>(true);
            m_GhostTypeHandle = systemState.GetComponentTypeHandle<GhostType>(true);
            m_SnapshotDataTypeHandle = systemState.GetComponentTypeHandle<SnapshotData>(true);
            m_SnapshotDataBufferTypeHandle = systemState.GetBufferTypeHandle<SnapshotDataBuffer>(true);
            m_SnapshotDynamicDataBufferTypeHandle = systemState.GetBufferTypeHandle<SnapshotDynamicDataBuffer>(true);
            m_LinkedEntityGroupTypeHandle = systemState.GetBufferTypeHandle<LinkedEntityGroup>(true);
            m_PreSpawnedGhostIndexTypeHandle = systemState.GetComponentTypeHandle<PreSpawnedGhostIndex>(true);
            m_PredictedGhostSpawnRequestTypeHandle = systemState.GetComponentTypeHandle<PredictedGhostSpawnRequest>(true);
            m_EntityTypeHandle = systemState.GetEntityTypeHandle();
        }

        /// <inheritdoc/>
        public void OnDestroy(ref SystemState systemState)
        {
            if (systemState.WorldUnmanaged.IsHost())
                return;
            m_LastInterpolatedTick.Dispose();
            m_AppliedPredictedTicks.Dispose();
            m_NumPredictedGhostWithNewData.Dispose();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState systemState)
        {
            var clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            if (SystemAPI.HasSingleton<ClientTickRate>())
                clientTickRate = SystemAPI.GetSingleton<ClientTickRate>();

            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var lastBackupTick = SystemAPI.GetSingleton<GhostSnapshotLastBackupTick>();
            var ghostHistoryPrediction = SystemAPI.GetSingleton<GhostPredictionHistoryState>();

            if (!networkTime.ServerTick.IsValid)
                return;

            var backupTick = lastBackupTick.Value;
            // Tick 后退时，备份可能比目标 Tick 更新；这种情况下不能使用该备份
            if (backupTick.IsValid && !networkTime.ServerTick.IsNewerThan(backupTick))
                backupTick = NetworkTick.Invalid;

            var interpolationTick = networkTime.InterpolationTick;
            var interpolationTickFraction = networkTime.InterpolationTickFraction;
            if (!m_ghostQuery.IsEmptyIgnoreFilter)
            {
                m_GhostComponentCollectionFromEntity.Update(ref systemState);
                m_GhostTypeCollectionFromEntity.Update(ref systemState);
                m_GhostComponentIndexFromEntity.Update(ref systemState);
                m_PredictedGhostTypeHandle.Update(ref systemState);
                m_GhostComponentTypeHandle.Update(ref systemState);
                m_GhostTypeHandle.Update(ref systemState);
                m_SnapshotDataTypeHandle.Update(ref systemState);
                m_SnapshotDataBufferTypeHandle.Update(ref systemState);
                m_SnapshotDynamicDataBufferTypeHandle.Update(ref systemState);
                m_LinkedEntityGroupTypeHandle.Update(ref systemState);
                m_PreSpawnedGhostIndexTypeHandle.Update(ref systemState);
                m_PredictedGhostSpawnRequestTypeHandle.Update(ref systemState);
                m_EntityTypeHandle.Update(ref systemState);
                var localNetworkId = SystemAPI.GetSingleton<NetworkId>().Value;
                UnsafeUtility.MemClear(m_NumPredictedGhostWithNewData.GetUnsafePtr(), m_NumPredictedGhostWithNewData.Length*sizeof(int));

                var predictedGhostWithNewDataJob = new CalculateNumPredictedGhostToRollback
                {
                    predictedGhostTypeHandle = m_PredictedGhostTypeHandle,
                    ghostSnapshotDataType = m_SnapshotDataTypeHandle,
                    ghostSnapshotDataBufferType = m_SnapshotDataBufferTypeHandle,
                    numPredictedGhostWithNewData = m_NumPredictedGhostWithNewData,
                    threadIndex = 0
                }.ScheduleParallel(m_PredictedGhostQuery, systemState.Dependency);
                var ghostCollection = SystemAPI.GetSingletonEntity<GhostCollection>();
                var updateJob = new UpdateJob
                {
                    GhostCollectionSingleton = ghostCollection,
                    GhostComponentCollectionFromEntity = m_GhostComponentCollectionFromEntity,
                    GhostTypeCollectionFromEntity = m_GhostTypeCollectionFromEntity,
                    GhostComponentIndexFromEntity = m_GhostComponentIndexFromEntity,
                    GhostTypeToCollectionIndex = systemState.EntityManager.GetComponentData<GhostCollection>(ghostCollection).GhostTypeToColletionIndex,
                    GhostMap = SystemAPI.GetSingleton<SpawnedGhostEntityMap>().Value,
#if UNITY_EDITOR || NETCODE_DEBUG
                    minMaxSnapshotTick = SystemAPI.GetSingletonRW<GhostStatsCollectionMinMaxTick>().ValueRO.Value,
#endif
                    numPredictedGhostWithNewData = m_NumPredictedGhostWithNewData,
                    interpolatedTargetTick = interpolationTick,
                    interpolatedTargetTickFraction = interpolationTickFraction,

                    predictedTargetTick = networkTime.ServerTick,
                    predictedTargetTickFraction = networkTime.ServerTickFraction,
                    appliedPredictedTicks = m_AppliedPredictedTicks.AsParallelWriter(),
                    PredictedGhostType = m_PredictedGhostTypeHandle,
                    lastPredictedTick = m_LastPredictedTick,
                    lastInterpolatedTick = m_LastInterpolatedTick.Value,

                    ghostInstanceTypeHandle = m_GhostComponentTypeHandle,
                    ghostTypeHandle = m_GhostTypeHandle,
                    ghostSnapshotDataType = m_SnapshotDataTypeHandle,
                    ghostSnapshotDataBufferType = m_SnapshotDataBufferTypeHandle,
                    ghostSnapshotDynamicDataBufferType = m_SnapshotDynamicDataBufferTypeHandle,
                    childEntityLookup = systemState.GetEntityStorageInfoLookup(),
                    linkedEntityGroupType = m_LinkedEntityGroupTypeHandle,
                    prespawnGhostIndexType = m_PreSpawnedGhostIndexTypeHandle,
                    predictedGhostRequestType = m_PredictedGhostSpawnRequestTypeHandle,

                    predictionStateBackupTick = backupTick,
                    predictionStateBackup = ghostHistoryPrediction.PredictionState,
                    predictionBackupEntityState = ghostHistoryPrediction.EntityData,
                    entityType = m_EntityTypeHandle,
                    ghostOwnerId = localNetworkId,
                    MaxExtrapolationTicks = clientTickRate.MaxExtrapolationTimeSimTicks,
                    netDebug = SystemAPI.GetSingleton<NetDebug>()
                };
                // TODO: 使用 BufferFromEntity
                var ghostComponentCollection = systemState.EntityManager.GetBuffer<GhostCollectionComponentType>(updateJob.GhostCollectionSingleton);
                DynamicTypeList.PopulateList(ref systemState, ghostComponentCollection, false, ref updateJob.DynamicTypeList); // 变更过滤在 Job 内按 Chunk 处理
                k_Scheduling.Begin();
                systemState.Dependency = updateJob.ScheduleParallelByRef(m_ghostQuery, predictedGhostWithNewDataJob);
                k_Scheduling.End();
            }

            m_LastPredictedTick = networkTime.ServerTick;
            if (networkTime.IsPartialTick)
                m_LastPredictedTick = NetworkTick.Invalid;

            // 如果已经收到本帧的插值目标，就可以更新最近一个已完整应用的插值 Tick
            m_NetworkSnapshotAckLookup.Update(ref systemState);
            var updateInterpolatedTickJob = new UpdateLastInterpolatedTick
            {
                AckFromEntity = m_NetworkSnapshotAckLookup,
                AckSingleton = SystemAPI.GetSingletonEntity<NetworkSnapshotAck>(),
                LastInterpolatedTick = m_LastInterpolatedTick,
                InterpolationTick = interpolationTick,
                InterpolationTickFraction = interpolationTickFraction
            };
            k_Scheduling.Begin();
            systemState.Dependency = updateInterpolatedTickJob.Schedule(systemState.Dependency);
            k_Scheduling.End();

            SystemAPI.GetSingletonRW<GhostUpdateVersion>().ValueRW.LastSystemVersion = systemState.LastSystemVersion;
        }
    }
}
