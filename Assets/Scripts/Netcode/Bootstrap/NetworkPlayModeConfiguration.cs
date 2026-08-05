namespace AnimarsCatcher.Networking
{
    using Unity.NetCode;

    /// <summary>
    /// 保存编辑器联机播放模式并向运行时启动流程提供只读配置
    /// </summary>
    public static class NetworkPlayModeConfiguration
    {
        public static bool HasEditorOverride { get; private set; }

        public static ClientServerBootstrap.PlayType PlayType { get; private set; }

        public static int ThinClientCount { get; private set; }

        public static bool IsServerOnly =>
            HasEditorOverride && PlayType == ClientServerBootstrap.PlayType.Server;

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
