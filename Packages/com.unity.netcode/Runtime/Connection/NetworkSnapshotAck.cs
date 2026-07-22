using System;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于升级到新组件类型的临时类型，将在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("NetworkSnapshotAckComponent has been deprecated. Use GhostInstance instead (UnityUpgradable) -> NetworkSnapshotAck", true)]
    public struct NetworkSnapshotAckComponent : IComponentData
    {}

    /// <summary>
    /// 客户端与服务器共用的组件，每个 NetworkId Entity 一个，用于保存客户端的 Snapshot Ack 和 Ping 信息
    /// </summary>
    public struct NetworkSnapshotAck : IComponentData
    {
        internal void UpdateReceivedByRemote(NetworkTick tick, uint mask, out int numSnapshotErrorsRequiringReset)
        {
            numSnapshotErrorsRequiringReset = 0;
            if (Hint.Unlikely(!tick.IsValid))
            {
                if (Hint.Unlikely(LastReceivedSnapshotByRemote.IsValid))
                {
                    numSnapshotErrorsRequiringReset = ReceivedSnapshotByRemoteMask.Length;
                    SnapshotPacketLoss.NumClientAckErrorsEncountered++;
                    ReceivedSnapshotByRemoteMask.Clear();
                    LastReceivedSnapshotByRemote = NetworkTick.Invalid;
                    FirstReceivedSnapshotByRemote = NetworkTick.Invalid;
                }
                return;
            }

            // 对上次保存 Tick 之后的任意 Tick，或再次收到相同 Tick 时，
            // 应按差值 shamt 把整个 Mask 左移，再把新 Mask 叠加到现有 Mask 上，
            // 因为客户端可能具有更新的 Ack 信息
            var shamt = Hint.Likely(LastReceivedSnapshotByRemote.IsValid) ? tick.TicksSince(LastReceivedSnapshotByRemote) : 0;
            if (Hint.Likely(shamt >= 0))
            {
                ReceivedSnapshotByRemoteMask.ShiftLeftExt(shamt);

                // 注意：直接覆盖 Mask 在逻辑上是有效的，因为客户端对指定 Tick 发送 true 后不应再发送 false
                // 但这里仍执行 OR 操作，以防客户端恶意发送或发生错误
                const int writeOffset = 0;
                const int numBitsToWrite = 32;
                var previousMask = ReceivedSnapshotByRemoteMask.GetBits(writeOffset, numBitsToWrite);
                mask |= (uint) previousMask;
                ReceivedSnapshotByRemoteMask.SetBits(writeOffset, mask, numBitsToWrite);
                LastReceivedSnapshotByRemote = tick;
                if (Hint.Unlikely(!FirstReceivedSnapshotByRemote.IsValid)) FirstReceivedSnapshotByRemote = tick;
                SnapshotPacketLoss.NumPacketsAcked += (ulong) (math.countbits(mask) - math.countbits(previousMask));
            }
            // 对更早的 Tick 不执行任何操作，因为客户端确实可能发送相对最近 Ack 为负的 Tick
            // 但受 Snapshot 隐含的顺序要求限制，它们无法正确包含新的 Ack 信息
        }

        /// <summary>
        /// 如果 <paramref name="tick"/> 对应的 Snapshot 已被接收，从客户端视角，
        /// 或已被确认，从服务器视角，则返回 true
        /// </summary>
        /// <param name="tick">要查询的 Tick</param>
        /// <returns><paramref name="tick"/> 对应的 Snapshot 是否已被接收或确认</returns>
        public bool IsReceivedByRemote(NetworkTick tick) => IsReceivedByRemote(tick, false);

        /// <summary>
        /// 如果 <paramref name="tick"/> 对应的 Snapshot 已被接收，从客户端视角，
        /// 或已被确认，从服务器视角，则返回 true
        /// </summary>
        /// <param name="tick">要查询的 Tick</param>
        /// <param name="backupValue">真实结果已经无法获知时使用的历史备用值
        /// 实际上，这只会发生在极少发送的静态优化 Ghost 上</param>
        /// <returns><paramref name="tick"/> 对应的 Snapshot 是否已被接收或确认</returns>
        public bool IsReceivedByRemote(NetworkTick tick, bool backupValue)
        {
            if (!tick.IsValid || !LastReceivedSnapshotByRemote.IsValid || !FirstReceivedSnapshotByRemote.IsValid)
                return false;
            int bit = LastReceivedSnapshotByRemote.TicksSince(tick);
            if (bit < 0)
                return false;
            if (bit >= ReceivedSnapshotByRemoteMask.Length)
            {
                // 以下是一项优化：早于 Buffer 历史范围的 Ack 很可能来自正在重新检查变化的静态优化 Ghost
                // 返回 false 会使它们无法通过 `CanUseStaticOptimization` 检查
                // 但由于相关信息已经丢失，此时无法确定该 Snapshot Tick 是否已确认
                // 此外，客户端可以在上面的流程中发出“重置全部 Ack Mask”信号，使之前的所有 Ack 失效

                // 因此使用额外数据推断该 Tick 是否已确认
                // 可以在以下任一情况下进行推断：
                // A）客户端从未发送“重置全部 Ack”事件，此时可以确认任意久远的 Tick
                // B）客户端至少发送过一次“重置全部 Ack”事件，可以检查最近的有效 Snapshot，
                // 即重置后的第一个 Snapshot，是否小于或等于正在检查的 Tick
                // 这样最多可以确认到 Tick 值自身精度的一半，在实际使用中等同于无限久远
                var isAllowedToInferInfinitelyFarBack = backupValue && SnapshotPacketLoss.NumClientAckErrorsEncountered == 0;
                var isVerifiablyGoodAck = backupValue && tick.TicksSince(FirstReceivedSnapshotByRemote) >= 0;
                return isAllowedToInferInfinitelyFarBack || isVerifiablyGoodAck;
            }
            var set = ReceivedSnapshotByRemoteMask.GetBits(bit) != 0;
            return set;
        }

        /// <summary>
        /// <para>从远端 Peer 收到的最近一个 Snapshot Tick</para>
        /// <para>对客户端而言，表示最近从服务器收到的 Snapshot</para>
        /// <para>对服务器而言，表示客户端收到的最近一个已确认数据包</para>
        /// </summary>
        public NetworkTick LastReceivedSnapshotByRemote;
        /// <summary>
        /// 表示远端连接收到的第一个有效 Snapshot
        /// 仅用于保护 Snapshot Ack，避免受到客户端“重置全部 Ack Mask”逻辑影响
        /// </summary>
        public NetworkTick FirstReceivedSnapshotByRemote;
        internal UnsafeBitArray ReceivedSnapshotByRemoteMask;

        /// <summary>
        /// <para>此字段在客户端和服务器上的含义不同：</para>
        /// <para>客户端：最近从服务器收到的 Ghost Snapshot</para>
        /// <para>服务器：最近收到的 Command Tick，用于丢弃乱序或迟到命令</para>
        /// </summary>
        public NetworkTick LastReceivedSnapshotByLocal;

        /// <summary>
        /// <para>
        /// 仅服务器使用，表示最近从客户端收到的完整 Tick 命令，用于调整 Command Age
        /// </para>
        /// </summary>
        internal NetworkTick MostRecentFullCommandTick;

        /// <summary>
        /// <para>客户端：记录此客户端收到的最近一个 Snapshot Sequence ID</para>
        /// <para>服务器：每次成功分派 Snapshot 时递增，此时假定已经发送</para>
        /// <para><see cref="SnapshotPacketLoss"/></para>
        /// </summary>
        public byte CurrentSnapshotSequenceId;
        /// <summary>
        /// 仅客户端使用的 Bitmask，表示最近 32 个 Snapshot 中哪些已经从服务器收到
        /// 在服务器上始终为 0
        /// </summary>
        public uint ReceivedSnapshotByLocalMask;
        /// <summary>
        /// 仅服务器使用，表示远端客户端加载的 Ghost Prefab 数量
        /// 客户端不使用此值，且始终为 0
        /// </summary>
        public uint NumLoadedPrefabs;

        /// <inheritdoc cref="SnapshotPacketLossStatistics"/>
        public SnapshotPacketLossStatistics SnapshotPacketLoss;

        /// <inheritdoc cref="CommandArrivalStatistics"/>
        public CommandArrivalStatistics CommandArrivalStatistics;

        /// <summary>
        /// 更新已加载 Prefab 数量，并同步远端连接的插值延迟
        /// </summary>
        /// <remarks>
        /// 如果 <paramref name="remoteTime"/> 小于 <see cref="LastReceivedRemoteTime"/>，则不修改组件状态，
        /// 因为这表示已经处理过更新的消息
        /// </remarks>
        /// <param name="remoteTime"></param>
        /// <param name="numLoadedPrefabs"></param>
        /// <param name="interpolationDelay"></param>
        internal void UpdateRemoteAckedData(uint remoteTime, uint numLoadedPrefabs, uint interpolationDelay)
        {
            // RPC 也会更新 Remote Time，并且无法保证 Snapshot 与 RPC 消息的处理顺序
            // 因此收到的 remoteTime 等于 LastReceivedRemoteTime 时也必须接受更新
            if (remoteTime != 0 && (!SequenceHelpers.IsNewer(LastReceivedRemoteTime, remoteTime) || LastReceivedRemoteTime == 0))
            {
                NumLoadedPrefabs = numLoadedPrefabs;
                RemoteInterpolationDelay = interpolationDelay;
            }
        }

        /// <summary>
        /// 根据 localTime 正确校验并计算 RTT
        /// </summary>
        /// <param name="localTime"></param>
        /// <param name="localTimeMinusRTT"></param>
        /// <returns>无效时返回 -1</returns>
        internal static int CalculateRttViaLocalTime(uint localTime, uint localTimeMinusRTT)
        {
            if (localTimeMinusRTT == 0)
                return -1;
            // 最高位被设置表示结果为负值，在低 Ping 下可能因客户端与服务器时钟差异而发生
            uint lastReceivedRTT = localTime - localTimeMinusRTT;
            if ((lastReceivedRTT & (1 << 31)) != 0)
                return -1;
            return (int) lastReceivedRTT;
        }

        /// <summary>
        /// 由于 RPC 可靠传输，它们可能在发送后经过多个 RTT 才到达
        /// 因此手动计算其 RTT 没有意义，这里改用 Transport 提供的功能
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="driver"></param>
        /// <param name="driverInstance"></param>
        /// <param name="pipelineStage"></param>
        /// <param name="reliableSequencedPipelineStageId"></param>
        /// <returns></returns>
        internal static unsafe int GetRpcRttFromReliablePipeline(NetworkStreamConnection connection,
            ref NetworkDriver driver, ref NetworkDriverStore.NetworkDriverInstance driverInstance,
            in NetworkPipeline pipelineStage, NetworkPipelineStageId reliableSequencedPipelineStageId)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            UnityEngine.Debug.Assert(pipelineStage.Id == driverInstance.reliablePipeline.Id);
#endif
            driver.GetPipelineBuffers(driverInstance.reliablePipeline, reliableSequencedPipelineStageId, connection.Value, out _, out _, out var sharedBuffer);
            var sharedCtx = (ReliableUtility.SharedContext*)sharedBuffer.GetUnsafePtr();
            // 注意：Transport 的 `RTTInfo` 值已经计入客户端 CPU 处理时间
            var rttInfo = sharedCtx->RttInfo;
            // 临时处理：如果该值与默认 `RTTInfo` 结构体完全相同，则丢弃
            // TODO 如果 Transport 提供判断默认 RTT 的方式，则修复此逻辑
            var isExactlyDefaultRttValue = rttInfo.SmoothedRtt == 50
                                           && rttInfo.LastRtt == 50
                                           && rttInfo.SmoothedVariance == 5
                                           && rttInfo.ResendTimeout == 50;
            return isExactlyDefaultRttValue ? -1 : rttInfo.LastRtt;
        }

        /// <summary>
        /// 保存收到消息或数据包时的本地时间，以及最近收到并将发回远端 Peer 的 Remote Time
        /// 同时更新连接的 <see cref="EstimatedRTT"/> 和 <see cref="DeviationRTT"/>
        /// </summary>
        /// <remarks>
        /// 如果 <paramref name="remoteTime"/> 小于 <see cref="LastReceivedRemoteTime"/>，则不修改组件状态，
        /// 因为这表示已经处理过更新的消息
        /// </remarks>
        /// <param name="remoteTime"></param>
        /// <param name="lastReceivedRTT">使用当前可用指标计算的 RTT，假定已经计入 CPU 处理时间</param>
        /// <param name="localTime"></param>
        internal void UpdateRemoteTime(uint remoteTime, int lastReceivedRTT, uint localTime)
        {
            // 由于 RPC 和 Snapshot 都用于同步时间，因此 remoteTime 等于最近接收值时也应更新统计信息
            if (remoteTime != 0 && (!SequenceHelpers.IsNewer(LastReceivedRemoteTime, remoteTime) || LastReceivedRemoteTime == 0))
            {
                LastReceivedRemoteTime = remoteTime;
                LastReceiveTimestamp = localTime;
                if (lastReceivedRTT < 0)
                    return;
                if (EstimatedRTT == 0)
                    EstimatedRTT = lastReceivedRTT;
                else
                    EstimatedRTT = EstimatedRTT * 0.875f + lastReceivedRTT * 0.125f;
                var latestDeviationRTT = math.abs(lastReceivedRTT - EstimatedRTT);
                DeviationRTT = DeviationRTT * 0.75f + latestDeviationRTT * 0.25f;
            }
        }

        /// <inheritdoc cref="CalculateSequenceIdDelta(byte,byte,bool)"/>
        internal readonly int CalculateSequenceIdDelta(byte current, bool isSnapshotConfirmedNewer) => CalculateSequenceIdDelta(current, CurrentSnapshotSequenceId, isSnapshotConfirmedNewer);

        /// <summary>
        /// 返回 <see cref="current"/> 与 <see cref="last"/> Sequence ID 之间的 Tick 差值，
        /// 前提是假定 <see cref="NetworkTime.ServerTick"/> 丢弃旧 Snapshot 的逻辑正确
        /// 因此：
        /// - 如果确认 Snapshot 更新，可以检查 0 到 byte.MaxValue 的差值
        /// - 如果确认 Snapshot 更旧，可以检查 0 到 -byte.MaxValue 的差值
        /// </summary>
        internal static int CalculateSequenceIdDelta(byte current, byte last, bool isSnapshotConfirmedNewer)
        {
            if (isSnapshotConfirmedNewer)
                return (byte)(current - last);
            return -(byte)(last - current);
        }

        /// <summary>
        /// 连接收到的最近一个远端时间戳
        /// Remote Time 会被发回远端，客户端通过 Command，服务器通过 Snapshot，
        /// 并用于计算连接的 RTT
        /// </summary>
        public uint LastReceivedRemoteTime;
        /// <summary>
        /// 连接收到最近一条消息时的本地时间戳
        /// 用于计算已流逝的处理时间，并上报给远端 Peer 以正确更新 RTT
        /// </summary>
        public uint LastReceiveTimestamp;
        /// <summary>
        /// 通过指数平滑计算的连接平均 RTT，单位为毫秒
        /// </summary>
        public float EstimatedRTT;
        /// <summary>
        /// RTT 相对 <see cref="EstimatedRTT"/> 的平均偏差，单位为毫秒
        /// 它不是真正的标准差，而是使用更简单指数平滑平均得到的近似值
        /// </summary>
        public float DeviationRTT;
        /// <summary>
        /// 服务器接收命令的迟到程度，使用 Q24:8 定点数表示
        /// 它衡量服务器收到命令时该命令落后服务器多少个 Tick，
        /// 并由 <see cref="NetworkTimeSystem"/> 用作同步 <see cref="NetworkTime.ServerTick"/> 的反馈，
        /// 使客户端始终运行在服务器之前
        /// 正数表示客户端落后于服务器，负数表示客户端领先于服务器
        /// </summary>
        public int ServerCommandAge;
        /// <summary>
        /// 客户端上报的插值延迟，单位为 Tick
        /// </summary>
        public uint RemoteInterpolationDelay;

        /// <summary>
        /// 计入本机处理时间后调整 <see cref="LastReceivedRemoteTime"/>
        /// </summary>
        /// <param name="localTime"></param>
        /// <returns></returns>
        internal readonly uint CalculateReturnTime(uint localTime)
        {
            var returnTime = LastReceivedRemoteTime;
            if (returnTime != 0)
            {
                var processingTime = (localTime - LastReceiveTimestamp);
                returnTime += processingTime;
            }
            return returnTime;
        }
    }
}
