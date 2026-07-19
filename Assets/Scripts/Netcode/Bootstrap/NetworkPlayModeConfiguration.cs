namespace AnimarsCatcher.Networking
{
    using Unity.NetCode;

    /// <summary>
    /// 保存编辑器联机播放模式并向运行时启动流程提供只读配置
    /// </summary>
    public static class NetworkPlayModeConfiguration
    {
        /// <summary>
        /// 获取编辑器是否已经写入播放模式配置
        /// </summary>
        public static bool HasEditorOverride { get; private set; }

        /// <summary>
        /// 获取编辑器请求创建的网络 World 组合
        /// </summary>
        public static ClientServerBootstrap.PlayType PlayType { get; private set; }

        /// <summary>
        /// 获取编辑器请求创建的 Thin Client 数量
        /// </summary>
        public static int ThinClientCount { get; private set; }

        /// <summary>
        /// 写入当前编辑器联机播放模式
        /// </summary>
        /// <param name="playType">需要创建的网络 World 组合</param>
        /// <param name="thinClientCount">需要创建的 Thin Client 数量</param>
        public static void ConfigureEditorPlayMode(
            ClientServerBootstrap.PlayType playType,
            int thinClientCount)
        {
            HasEditorOverride = true;
            PlayType = playType;
            ThinClientCount = thinClientCount;
        }
    }
}
