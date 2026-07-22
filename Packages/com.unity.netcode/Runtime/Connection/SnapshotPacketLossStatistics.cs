using Unity.Collections;
using Unity.Mathematics;

namespace Unity.NetCode
{
    /// <summary>
    /// 保存所有已接收 Snapshot 的丢包原因和统计信息，因此除一项例外外仅客户端使用
    /// 通过 <see cref="NetworkSnapshotAck"/> 访问
    /// </summary>
    /// <remarks>实现方式与 <see cref="Unity.Networking.Transport.UnreliableSequencedPipelineStage"/> 统计信息非常相似</remarks>
    public struct SnapshotPacketLossStatistics
    {
        /// <summary>
        ///     在客户端上，统计该客户端从服务器收到的 Snapshot Packet 数量
        ///     在服务器上，保存已发送 Snapshot 数量
        /// </summary>
        public ulong NumPacketsReceived;
        /// <summary>
        /// 仅服务器使用，保存客户端成功回复已确认的 Snapshot 数量
        /// </summary>
        public ulong NumPacketsAcked;
        /// <summary>
        /// 统计因 Sequence ID 无效而丢弃的 Snapshot Packet 数量，即数据包已经到达但顺序错误
        /// </summary>
        public ulong NumPacketsCulledOutOfOrder;
        /// <summary>
        /// NetCode 包每个渲染帧只能处理一个 Snapshot
        /// 如果同一帧到达两个或更多 Snapshot，会删除除一个之外的全部 Snapshot，且不处理它们
        /// 因此当连接抖动高于一个 <see cref="ClientServerTickRate.NetworkTickRate"/> 间隔时，这种丢包很常见
        /// 例如抖动为 ±20 ms，而 NetworkTickRate 为 60 Hz（16.67 ms）时，会发生大量数据包覆盖
        /// </summary>
        /// <remarks>这种情况也称为 Packet Burst</remarks>
        public ulong NumPacketsCulledAsArrivedOnSameFrame;
        /// <summary>
        /// 检测 <see cref="NetworkSnapshotAck.CurrentSnapshotSequenceId"/> 中的间断，以判断真实丢包
        /// </summary>
        public ulong NumPacketsDroppedNeverArrived;
        /// <summary>
        /// 表示客户端报告 Snapshot Ack 错误并导致 Ack History Buffer 必须重置的次数
        /// </summary>
        public ulong NumClientAckErrorsEncountered;

        /// <summary>
        /// 仅服务器使用，客户端已确认的 Snapshot Packet 占全部已发送 Snapshot Packet 的百分比
        /// </summary>
        public double AckPercent => NumPacketsReceived != 0 ? NumPacketsAcked / (double) (NumPacketsReceived) : 0;
        /// <summary>
        /// 根据 Sequence ID 推定已发送给我们的全部 Snapshot Packet 中，因网络丢包而丢失的百分比
        /// </summary>
        public double NetworkPacketLossPercent => NumPacketsReceived != 0 ? NumPacketsDroppedNeverArrived / (double) (NumPacketsReceived + NumPacketsDroppedNeverArrived) : 0;
        /// <summary>
        /// 根据 Sequence ID 推定已发送给我们的全部 Snapshot Packet 中，因乱序到达而被丢弃的百分比
        /// </summary>
        public double OutOfOrderPacketLossPercent => NumPacketsReceived != 0 ? NumPacketsCulledOutOfOrder / (double) (NumPacketsReceived + NumPacketsDroppedNeverArrived) : 0;
        /// <summary>
        /// 根据 Sequence ID 推定已发送给我们的全部 Snapshot Packet 中，因与另一 Snapshot 同帧到达而被丢弃的百分比
        /// </summary>
        public double ArrivedOnTheSameFrameClobberedPacketLossPercent => NumPacketsReceived != 0 ? NumPacketsCulledAsArrivedOnSameFrame / (double) (NumPacketsReceived + NumPacketsDroppedNeverArrived) : 0;
        /// <summary>
        /// 根据 Sequence ID 推定已发送给我们的全部 Snapshot Packet 中，因任意原因被丢弃的百分比
        /// </summary>
        public double CombinedPacketLossPercent => NumPacketsReceived != 0 ? (CombinedPacketLossCount) / (double) (NumPacketsReceived + NumPacketsDroppedNeverArrived) : 0;
        /// <summary>
        /// 以任意形式丢失的数据包数量
        /// </summary>
        public ulong CombinedPacketLossCount => NumPacketsDroppedNeverArrived + NumPacketsCulledOutOfOrder + NumPacketsCulledAsArrivedOnSameFrame;

        /// <summary>
        /// 将两个 SnapshotPacketLossStatistics 相加
        /// </summary>
        /// <param name="a">第一个 SnapshotPacketLossStatistics</param>
        /// <param name="b">第二个 SnapshotPacketLossStatistics</param>
        /// <returns>两个 SnapshotPacketLossStatistics 相加的结果</returns>
        public static SnapshotPacketLossStatistics operator +(SnapshotPacketLossStatistics a, SnapshotPacketLossStatistics b)
        {
            a.NumPacketsReceived += b.NumPacketsReceived;
            a.NumPacketsAcked += b.NumPacketsAcked;
            a.NumPacketsCulledOutOfOrder += b.NumPacketsCulledOutOfOrder;
            a.NumPacketsCulledAsArrivedOnSameFrame += b.NumPacketsCulledAsArrivedOnSameFrame;
            a.NumPacketsDroppedNeverArrived += b.NumPacketsDroppedNeverArrived;
            return a;
        }

        /// <summary>
        /// 将两个 SnapshotPacketLossStatistics 相减
        /// </summary>
        /// <param name="a">第一个 SnapshotPacketLossStatistics</param>
        /// <param name="b">第二个 SnapshotPacketLossStatistics</param>
        /// <returns>两个 SnapshotPacketLossStatistics 相减的结果</returns>
        public static SnapshotPacketLossStatistics operator -(SnapshotPacketLossStatistics a, SnapshotPacketLossStatistics b)
        {
            // 保护减法结果，因为每 3 秒轮询时可能出现负值
            a.NumPacketsReceived -= math.min(a.NumPacketsReceived, b.NumPacketsReceived);
            a.NumPacketsAcked -= math.min(a.NumPacketsAcked, b.NumPacketsAcked);
            a.NumPacketsCulledOutOfOrder -= math.min(a.NumPacketsCulledOutOfOrder, b.NumPacketsCulledOutOfOrder);
            a.NumPacketsCulledAsArrivedOnSameFrame -= math.min(a.NumPacketsCulledAsArrivedOnSameFrame, b.NumPacketsCulledAsArrivedOnSameFrame);
            a.NumPacketsDroppedNeverArrived -= math.min(a.NumPacketsDroppedNeverArrived, b.NumPacketsDroppedNeverArrived);
            return a;
        }

        /// <summary>
        /// 此 World 类型的格式化统计信息转储
        /// </summary>
        /// <returns>此 World 类型的格式化统计信息转储</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString512Bytes ToFixedString()
        {
            if (NumPacketsReceived == 0) return "SPLS[default]";
            if (NumPacketsAcked > 0) return $"SPLS[sent:{NumPacketsReceived}, receivedAck:{NumPacketsAcked} {(int) (AckPercent * 100)}%]";
            return $"SPLS[received:{NumPacketsReceived}, combinedPL:{CombinedPacketLossCount} {(int) (CombinedPacketLossPercent * 100)}%, networkPL:{NumPacketsDroppedNeverArrived} {(int) (NetworkPacketLossPercent * 100)}%, outOfOrderPL:{NumPacketsCulledOutOfOrder} {(int) (OutOfOrderPacketLossPercent * 100)}%, clobberedPL:{NumPacketsCulledAsArrivedOnSameFrame} {(int) (ArrivedOnTheSameFrameClobberedPacketLossPercent * 100)}%]";
        }

        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }
}
