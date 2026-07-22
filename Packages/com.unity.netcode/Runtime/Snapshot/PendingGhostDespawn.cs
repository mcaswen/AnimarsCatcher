#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Profiling;

namespace Unity.NetCode
{
    /// <summary>
    /// 处理 Ghost Snapshot 中 Despawn 消息的发送
    /// </summary>
    internal struct PendingGhostDespawn : IComparable<PendingGhostDespawn>
    {
        private static readonly ProfilerMarker s_AckInFlightDespawns = new ("PendingGhostDespawn-AckInFlightDespawns");
        private static readonly ProfilerMarker s_FindNewDespawns = new ("PendingGhostDespawn-FindNewDespawns");
        private static readonly ProfilerMarker s_FindNewPrespawnDespawns = new ("PendingGhostDespawn-FindNewPrespawnDespawns");
        private static readonly ProfilerMarker s_SortDespawns = new ("PendingGhostDespawn-Sort");
        private static readonly ProfilerMarker s_WriteDespawns = new ("PendingGhostDespawn-Write");
        private static readonly ProfilerMarker s_WriteDespawnsMarker = new ("PendingGhostDespawn-WriteDespawns");
        private static readonly ProfilerMarker s_FindOldestMarker = new ("PendingGhostDespawn-FindOldestTick");
        /// <summary>
        /// 表示单个 Ghost Despawn 同时允许处于传输中的最大消息数
        /// 对应 <see cref="DespawnSlot0"/> 和 <see cref="DespawnSlot1"/>
        /// </summary>
        private const int k_MaxInFlight = 2;

        /// <summary>
        /// 这是一个压缩技巧：由于 GhostId 已排序，编码 GhostId 差值时通常可以预期下一个值
        /// 至少比上一个值大 1，因此记录上一个 GhostId 时加上该常量可以减小平均差值
        /// 从而使用更少的位并获得更好的压缩率
        /// </summary>
        internal const int k_ExpectedGhostIdDelta = 1;

        public enum DespawnReason : byte
        {
            /// <summary>
            /// Ghost Entity 已销毁
            /// </summary>
            EntityDestroyed = 1,
            /// <summary>
            /// Ghost 与当前连接不再相关
            /// </summary>
            Irrelevant = 2,
            /// <summary>
            /// 预生成场景已在服务器和客户端中的一端或两端卸载
            /// </summary>
            PrespawnSceneUnloaded = 3,
        }
        /// <summary>
        /// Despawn Snapshot 槽位 0
        /// </summary>
        internal NetworkTick DespawnSlot0;
        /// <summary>
        /// Despawn Snapshot 槽位 1
        /// </summary>
        internal NetworkTick DespawnSlot1;

        /// <summary>
        /// 正在 Despawn 的 Ghost 详细信息
        /// </summary>
        internal GhostCleanup Ghost;
        /// <summary>
        /// 当前处于传输中的 Despawn 消息数量
        /// </summary>
        internal byte CountInFlight;
        /// <summary>
        /// Despawn 原因
        /// </summary>
        public DespawnReason Reason;

        internal static uint WriteDespawns(NetworkTick currentTick, ref UnsafeList<PendingGhostDespawn> pending,
            ref ConnectionStateData.GhostStateList ghostStateData, NativeList<ArchetypeChunk> despawnChunks,
            ref NetworkSnapshotAck ack, ComponentTypeHandle<GhostCleanup> ghostSystemStateType,
            ref DataStreamWriter dataStream, ref StreamCompressionModel compressionModel,
            ref UnsafeList<PrespawnHelper.GhostIdInterval> newLoadedPrespawnRanges, ref NativeList<int> prespawnDespawns,
            ref GhostSendSystemData systemData
#if NETCODE_DEBUG
            , ref PacketDumpLogger netDebugPacket
#endif
            )
        {
            using var m = s_WriteDespawnsMarker.Auto();
            var oldestPendingGhostsDespawnTick = ack.LastReceivedSnapshotByRemote;
            if (oldestPendingGhostsDespawnTick.IsValid)
                oldestPendingGhostsDespawnTick.Increment();

#if NETCODE_DEBUG
            int despawnsAcked = pending.Length;
#endif
            // 先用已确认的 Despawn 和本地新发现的 Despawn 刷新列表
            // 然后对 Despawn 列表排序并尽可能多地发送

            // 获取 Snapshot Ack，并据此移除尽可能多的已确认传输中 Despawn 消息
            if (!pending.IsEmpty)
            {
                using var _ = s_AckInFlightDespawns.Auto();
                for (var i = 0; i < pending.Length; i++)
                {
                    ref var pendingDespawn = ref pending.ElementAt(i);
                    pendingDespawn.AssertValid();
                    if (pendingDespawn.ClientAckedAnyInFlight(ref ack))
                    {
                        ref var state = ref ghostStateData.GetGhostState(pendingDespawn.Ghost.ghostId, pendingDespawn.Ghost.spawnTick);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        var wasDespawning = (state.Flags & ConnectionStateData.GhostStateFlags.IsDespawning) != 0;
                        UnityEngine.Debug.Assert(wasDespawning, "wasDespawning");
#endif
                        state.Flags &= ~(ConnectionStateData.GhostStateFlags.IsDespawning);
                        state.Flags |= ConnectionStateData.GhostStateFlags.HasBeenDespawnedAtLeastOnce;
                        pending.RemoveAtSwapBack(i);
                        i--;
                    }
                }
            }

#if NETCODE_DEBUG
            despawnsAcked = pending.Length - despawnsAcked;
#endif

            // 在 despawnChunks 中查找新的 Despawn
            if (!despawnChunks.IsEmpty)
            {
                using var _ = s_FindNewDespawns.Auto();
                for (var chunk = 0; chunk < despawnChunks.Length; ++chunk)
                {
                    var ghostStates = despawnChunks[chunk].GetNativeArray(ref ghostSystemStateType);
                    for (var ent = 0; ent < ghostStates.Length; ++ent)
                    {
                        var ghostCleanup = ghostStates[ent];
                        ref var state = ref ghostStateData.GetGhostState(ghostCleanup);
                        var isRelevant = (state.Flags & ConnectionStateData.GhostStateFlags.IsRelevant) != 0;
                        var isAlreadyDespawning = (state.Flags & ConnectionStateData.GhostStateFlags.IsDespawning) != 0;

                        if (isRelevant && !isAlreadyDespawning)
                        {
                            // TODO: 确认是否需要清空 Snapshot 历史 Buffer
                            AddNewPendingDespawn(ref pending, ref state.Flags, ghostCleanup, DespawnReason.EntityDestroyed);
                        }
                    }
                }
            }

            // 针对所有新客户端已加载的场景，发送当前已销毁预生成 Entity 的列表
            // TODO: 重构预生成 Entity 的 Despawn 逻辑以移除此流程
            if (prespawnDespawns.Length > 0 && newLoadedPrespawnRanges.Length > 0)
            {
                using var _ = s_FindNewPrespawnDespawns.Auto();
                for (int i = 0; i < prespawnDespawns.Length; ++i)
                {
                    // 如果不在任何新区间中则跳过
                    var ghostId = prespawnDespawns[i];
                    if(ghostId < newLoadedPrespawnRanges[0].Begin ||
                       ghostId > newLoadedPrespawnRanges[newLoadedPrespawnRanges.Length-1].End)
                        continue;

                    // TODO: 可以使用类似 C++ lower_bound 的二分查找
                    int idx = 0;
                    while (idx < newLoadedPrespawnRanges.Length && ghostId > newLoadedPrespawnRanges[idx].End)
                        ++idx;

                    if (idx < newLoadedPrespawnRanges.Length)
                    {
                        ref var state = ref ghostStateData.GetPrespawnGhostState(ghostId);
                        // 特殊情况：SubScene 已重新加载，因此需要重新发送 Despawn
                        // TODO: 可将 newLoadedPrespawnRanges 中的所有 Ghost 标记为相关以简化此逻辑
                        bool hasBeenDespawnedBefore = (state.Flags & ConnectionStateData.GhostStateFlags.HasBeenDespawnedAtLeastOnce) != 0;
                        if (hasBeenDespawnedBefore)
                        {
                            state.Flags |= ConnectionStateData.GhostStateFlags.IsRelevant;
                            AddNewPendingDespawn(ref pending, ref state.Flags, new GhostCleanup
                            {
                                ghostId = ghostId,
                                spawnTick = NetworkTick.Invalid,
                                despawnTick = NetworkTick.Invalid,
                            }, DespawnReason.PrespawnSceneUnloaded);
                        }
                    }
                }
            }

            // Pending Despawn 列表更新完成后，根据所有待处理项更新 oldestPendingGhostsDespawnTick
            if (!pending.IsEmpty)
            {
                using var a = s_FindOldestMarker.Auto();
                for (int i = 0; i < pending.Length; i++)
                {
                    ref var pendingDespawn = ref pending.ElementAt(i);
                    pendingDespawn.AssertValid();
                    if (pendingDespawn.Ghost.despawnTick.IsValid
                        && (!oldestPendingGhostsDespawnTick.IsValid || oldestPendingGhostsDespawnTick.IsNewerThan(pendingDespawn.Ghost.despawnTick)))
                    {
                        oldestPendingGhostsDespawnTick = pendingDespawn.Ghost.despawnTick;
                    }
                }
            }

            // 尽可能多地发送 Despawn
            uint despawnLen = 0;
            if(!pending.IsEmpty)
            {
                using var _ = s_WriteDespawns.Auto();
                systemData.PercentReservedForDespawnMessages = math.clamp(systemData.PercentReservedForDespawnMessages,
                    GhostSystemConstants.MinPercentReservedForDespawnMessages, GhostSystemConstants.MaxPercentReservedForDespawnMessages);
                const ushort minBytesAssignedToDespawns = 10;
                const int minBytesLeftForSnapshotOverhead = 8;
                const ushort maxCanFitInLength = ushort.MaxValue;
                var maxBytesUsedForDespawns = (ushort)math.clamp(dataStream.Capacity * systemData.PercentReservedForDespawnMessages, minBytesAssignedToDespawns, maxCanFitInLength);

                s_SortDespawns.Begin();
                pending.Sort();
                s_SortDespawns.End();

#if NETCODE_DEBUG
                FixedString128Bytes despawnTitle = $"\tST:{currentTick.ToFixedString()} [Despawn GIDs] ";
                FixedString512Bytes despawnLog = despawnTitle;
                int despawnBits = dataStream.LengthInBits;
#endif
                int nextExpectedGhostId = k_ExpectedGhostIdDelta;
                for (var i = 0; i < pending.Length; i++)
                {
                    ref var pendingDespawn = ref pending.ElementAt(i);
                    if (pendingDespawn.CountInFlight >= k_MaxInFlight // 已到达排序后具有最大传输中消息数的条目
                        || dataStream.Length + minBytesLeftForSnapshotOverhead >= maxBytesUsedForDespawns)
                    {
#if NETCODE_DEBUG
                        if (netDebugPacket.IsCreated)
                        {
                            despawnLog.Append((FixedString128Bytes)$"Hit DespawnMax! Writer:{dataStream.Length}B+{minBytesAssignedToDespawns}B>={maxBytesUsedForDespawns}B ({dataStream.Capacity}B*{(int)(systemData.PercentReservedForDespawnMessages*100)}%)!");
                        }
#endif
                        break;
                    }

                    // 注意：虽然待处理 GhostId 会按 GhostId 升序排列，但首先按传输中消息数量排序
                    // 发送次数较少的条目具有更高优先级，例如：
                    // [ServerTick:1] 发送新的 Despawn 3、4、5
                    // [ServerTick:2] 先发送尚未发送的 Despawn 10、11、12，再重发 3、4、5
                    // 因此差值必须使用 `int`，不能假定其为 `uint`
                    dataStream.WritePackedIntDelta(pendingDespawn.Ghost.ghostId, nextExpectedGhostId, compressionModel);
                    nextExpectedGhostId = pendingDespawn.Ghost.ghostId + k_ExpectedGhostIdDelta;
                    pendingDespawn.TrackWriteOfDespawn(currentTick);
                    despawnLen++;
#if NETCODE_DEBUG
                    if (netDebugPacket.IsCreated)
                    {
                        despawnLog.Append(pendingDespawn.Ghost.ghostId);
                        despawnLog.Append(':');
                        despawnLog.Append(pendingDespawn.Reason switch
                        {
                            DespawnReason.Irrelevant => 'I',
                            DespawnReason.EntityDestroyed => 'D',
                            DespawnReason.PrespawnSceneUnloaded => 'U',
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            _ => throw new InvalidOperationException("Missing enum entry."),
#else
                            _ => '?',
#endif
                        });
                        despawnLog.Append(pendingDespawn.CountInFlight);
                        despawnLog.Append(' ');

                        if (despawnLog.Length > (despawnLog.Capacity >> 1))
                        {
                            netDebugPacket.Log(despawnLog);
                            despawnLog = despawnTitle;
                        }
                    }
#endif
                }

#if NETCODE_DEBUG
                if (despawnLen > 0 && netDebugPacket.IsCreated)
                {
                    despawnBits = dataStream.LengthInBits - despawnBits;
                    despawnLog.Append((FixedString128Bytes) $"\n\tST:{currentTick.ToFixedString()} [Despawn] Sending:{despawnLen} of Pending:{pending.Length} in ~{(despawnBits/8)}B ({despawnBits} bits, ~{(int)((float)despawnBits/despawnLen)} bits/gid), despawnsAcked:{despawnsAcked}!");
                    netDebugPacket.Log(despawnLog);
                }
#endif
            }

            // 当所有客户端都确认 Chunk 内最新 despawnTick 对应的全部 Despawn 后，才能删除 despawnChunk
            // 因此这里为当前连接记录尚未确认 Ghost 中最旧的 despawnTick
            ghostStateData.OldestPendingDespawnTick = oldestPendingGhostsDespawnTick;

            return despawnLen;
        }

        /// <summary>
        /// 尝试确认任意处于传输中的 Snapshot
        /// 同时重置因 Snapshot 丢失而确认失败的所有槽位
        /// </summary>
        /// <param name="ack"></param>
        /// <returns>成功确认时返回 true</returns>
        private bool ClientAckedAnyInFlight(ref NetworkSnapshotAck ack)
        {
            if (CountInFlight == 0) return false;
            if (AckOrResetInFlightSlot(ref DespawnSlot0, ref CountInFlight, ref ack)) return true;
            if (AckOrResetInFlightSlot(ref DespawnSlot1, ref CountInFlight, ref ack)) return true;
            //if (AckOrResetInFlightSlot(ref DespawnSlot2, ref CountInFlight, ref ack)) return true;
            return false;

            static bool AckOrResetInFlightSlot(ref NetworkTick slot, ref byte countInFlight, ref NetworkSnapshotAck ack)
            {
                if (!slot.IsValid || !ack.LastReceivedSnapshotByRemote.IsValid)
                {
                    // Snapshot 尚未发送或还不能确认
                    return false;
                }
                if (slot.IsNewerThan(ack.LastReceivedSnapshotByRemote))
                {
                    // 客户端尚未返回该 Snapshot 或任何后续 Snapshot 的 Ack，因此必须继续等待
                    // 这些 Snapshot 仍处于传输中
                    return false;
                }
                if (ack.IsReceivedByRemote(slot))
                {
                    // Ack 成功
                    return true;
                }

                // 客户端丢失了该槽位的 Snapshot，因此重置槽位条目
                slot = NetworkTick.Invalid;
                countInFlight--;
                return false;
            }
        }

        /// <summary>
        /// 记录已经将此 Ghost 的 Despawn 消息写入由 <see cref="currentTick"/> 标识的 Snapshot
        /// </summary>
        /// <param name="currentTick">写入 Despawn 消息的 Snapshot Tick</param>
        /// <exception cref="InvalidOperationException"></exception>
        private void TrackWriteOfDespawn(NetworkTick currentTick)
        {
            AssertValid();
            if (TryAddDespawnWrite(ref DespawnSlot0, ref CountInFlight, currentTick)) return;
            if (TryAddDespawnWrite(ref DespawnSlot1, ref CountInFlight, currentTick)) return;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            throw new InvalidOperationException("No slots left!");
#endif

            static bool TryAddDespawnWrite(ref NetworkTick slot, ref byte countInFlight, NetworkTick currentTick)
            {
                if (!slot.IsValid)
                {
                    slot = currentTick;
                    countInFlight++;
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 先按发送次数升序排列
        /// 再按 GhostId 升序排列
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public int CompareTo(PendingGhostDespawn other)
        {
            var countDelta = CountInFlight.CompareTo(other.CountInFlight);
            return countDelta != 0 ? countDelta : Ghost.ghostId.CompareTo(other.Ghost.ghostId);
        }

        /// <summary>
        /// 为每个需要开始 Despawn 的 Ghost 调用
        /// </summary>
        /// <param name="pendingDespawns">待处理 Despawn 列表</param>
        /// <param name="flags">该 Ghost 实例的标志</param>
        /// <param name="ghostCleanup">Ghost 详细信息的副本</param>
        /// <param name="reason">仅用于调试的 Despawn 原因</param>
        public static void AddNewPendingDespawn(ref UnsafeList<PendingGhostDespawn> pendingDespawns,
            ref ConnectionStateData.GhostStateFlags flags, in GhostCleanup ghostCleanup, DespawnReason reason)
        {
            var isRelevant = (flags & ConnectionStateData.GhostStateFlags.IsRelevant) != 0;
            var isAlreadyDespawning = (flags & ConnectionStateData.GhostStateFlags.IsDespawning) != 0;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            UnityEngine.Debug.Assert(isRelevant, "isRelevant");
#endif
            // 如果已标记为 Despawn，则无需重复添加，但会丢失新的原因信息
            if (Hint.Unlikely(isAlreadyDespawning))
            {
                // 如果需要区分已销毁 Ghost 与因不相关触发的 Despawn，则必须处理这种情况
                return;
            }

            // 更新标志
            flags &= (~ConnectionStateData.GhostStateFlags.IsRelevant);
            flags |= ConnectionStateData.GhostStateFlags.IsDespawning | ConnectionStateData.GhostStateFlags.HasBeenDespawnedAtLeastOnce;
            pendingDespawns.Add(new PendingGhostDespawn
            {
                Ghost = ghostCleanup,
                Reason = reason,
            });
            pendingDespawns[^1].AssertValid();
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        [Conditional("UNITY_ASSERTIONS")]
        private void AssertValid()
        {
            // 检查计数
            UnityEngine.Debug.Assert(CountInFlight <= k_MaxInFlight, "k_MaxInFlight");
            UnityEngine.Debug.Assert(CountInFlight ==
                                     (DespawnSlot0.IsValid ? 1 : 0) +
                                     (DespawnSlot1.IsValid ? 1 : 0), "CountInFlight");// +
                                     //(DespawnSlot2.IsValid ? 1 : 0);
            // 检查重复槽位
            UnityEngine.Debug.Assert(!DespawnSlot0.IsValid || DespawnSlot0 != DespawnSlot1, "NoDup0vs1");
            // UnityEngine.Debug.Assert(!DespawnSlot0.IsValid || DespawnSlot0 != DespawnSlot2);
            // UnityEngine.Debug.Assert(!DespawnSlot1.IsValid || DespawnSlot1 != DespawnSlot2);

            // 检查 Ghost 有效性
            UnityEngine.Debug.Assert(Reason != default, "Reason");
            UnityEngine.Debug.Assert(Ghost.ghostId != default, "ghostId");
            // TODO: 保证 spawnTick 与 despawnTick 始终有效，以便在此断言
        }

        /// <summary>
        /// 撤销当前 Tick 的所有 Snapshot Despawn 写入
        /// </summary>
        /// <param name="pendingDespawns"></param>
        /// <param name="currentTick"></param>
        public static void RevertSnapshotDespawnWrites(ref UnsafeList<PendingGhostDespawn> pendingDespawns, NetworkTick currentTick)
        {
            for (int i = 0; i < pendingDespawns.Length; i++)
            {
                ref var pending = ref pendingDespawns.ElementAt(i);
                pending.AssertValid();
                if (pending.CountInFlight <= 0) continue;
                RevertIfSameTick(ref pending.DespawnSlot0, ref pending.CountInFlight, currentTick);
                RevertIfSameTick(ref pending.DespawnSlot1, ref pending.CountInFlight, currentTick);
                //RevertIfSameTick(ref pending.DespawnSlot2, ref pending.CountInFlight, currentTick);
                pending.AssertValid();
            }

            static void RevertIfSameTick(ref NetworkTick slot, ref byte countInFlight, in NetworkTick tick)
            {
                if (slot == tick)
                {
                    slot = NetworkTick.Invalid;
                    countInFlight--;
                }
            }
        }
    }
}
