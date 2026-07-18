namespace AnimarsCatcher.Animars.Fsm
{
    /// <summary>
    /// 为 Ani 业务状态机划分互不重叠的标识符区间
    /// </summary>
    public static class FsmIdSpace
    {
        public const ushort Block = 256;
        public const ushort AniMovementBase = Block * 1;
        public const ushort PickerAniBase = Block * 2;

        /// <summary>
        /// 将模块基址和局部索引组合为全局状态机标识符
        /// </summary>
        /// <param name="base">模块标识符基址</param>
        /// <param name="local">模块内部局部索引</param>
        /// <returns>全局唯一的标识符</returns>
        public static ushort Of(ushort @base, ushort local)
        {
            return (ushort)(@base + local);
        }
    }
}
