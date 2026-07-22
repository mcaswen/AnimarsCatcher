namespace Unity.NetCode.Tests
{
    /// <summary>
    /// 当 ACK 完全可靠且即时返回时，静态优化可能只是表面上正常工作
    /// 例如 ServerTick 为 3 时，ServerTick 2 的 Snapshot 已被确认，因此还需要覆盖其他网络条件
    /// </summary>
    internal enum NetCodeTestLatencyProfile
    {
        None,
        /// <summary>
        /// 往返时间为 60ms，向上取整为 4 个 Tick
        /// </summary>
        RTT60ms,
        /// <summary>
        /// 丢包率为 33%，即每 3 个包丢失 1 个
        /// </summary>
        PL33,
        /// <summary>
        /// 每个方向延迟 16ms，约为 1 个 Tick，丢包率为 5%，即每 20 个包丢失 1 个
        /// </summary>
        RTT16ms_PL5,
    }
}
