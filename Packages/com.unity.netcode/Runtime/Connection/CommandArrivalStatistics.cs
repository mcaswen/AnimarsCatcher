using Unity.Collections;

namespace Unity.NetCode
{
    /// <summary>
    /// 保存从客户端接收命令的频率与可靠性统计信息
    /// 因此仅在服务器上有效，可用于诊断输入或命令问题
    /// </summary>
    public struct CommandArrivalStatistics
    {
        // TODO 增加预期到达命令数量统计，让用户能够查看命令丢失情况
        /// <summary>
        /// 已到达 Command Packet 的总数
        /// 为提供冗余，单个 NetCode Command Packet 通常包含多条命令
        /// </summary>
        public int NumCommandPacketsArrived;

        /// <summary>
        /// 已到达命令的总数
        /// 为提供冗余，单个 NetCode Command Packet 通常包含多条命令
        /// </summary>
        public uint NumCommandsArrived;

        /// <summary>
        /// 已到达命令中有多少是对已接收命令的重复发送
        /// </summary>
        public uint NumRedundantResends;

        /// <summary>
        /// 未能及时到达并被使用的单条命令数量，因此发送这些命令已经没有意义
        /// 为提供冗余，单个 NetCode Command Packet 通常包含多条命令
        /// </summary>
        /// <remarks>使用此字段优化 <see cref="ClientTickRate.NumAdditionalCommandsToSend"/></remarks>
        public uint NumArrivedTooLate;

        /// <summary>
        /// 已接收输入数据包，即 Command Packet 的 Payload 大小滚动平均值，不包含 Transport Header
        /// </summary>
        public float AvgCommandPayloadSizeInBits;

        /// <summary>
        /// 到达过晚的命令百分比
        /// </summary>
        public double ArrivedTooLatePercent => NumCommandsArrived != 0 ? ((double) NumArrivedTooLate / NumCommandsArrived) : 0;

        /// <summary>
        /// 冗余重发命令的百分比
        /// </summary>
        public double ResendPercent => NumCommandsArrived != 0 ? ((double) NumRedundantResends / NumCommandsArrived) : 0;

        /// <summary>
        /// 每个数据包平均包含的命令数量
        /// </summary>
        public double AvgCommandsPerPacket => NumCommandPacketsArrived != 0 ? ((double)NumCommandsArrived / NumCommandPacketsArrived) : 0;

        /// <summary>
        /// 调试字符串
        /// </summary>
        /// <returns>格式化后的调试字符串</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString128Bytes ToFixedString()
        {
            var commandsPerPacket = AvgCommandsPerPacket;
            var resendPercent = (int) (ResendPercent * 100);
            var tooLatePercent = (int) (ArrivedTooLatePercent * 100);
            return $"CAS[packets:{NumCommandPacketsArrived},commands:{NumCommandsArrived},avgCommandsPerPacket:{commandsPerPacket},resends:{NumRedundantResends} {resendPercent}%,late:{NumArrivedTooLate} {tooLatePercent}%,avgSize:{CommandDataUtility.FormatBitsBytes((int)AvgCommandPayloadSizeInBits)}]";
        }

        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }
}
