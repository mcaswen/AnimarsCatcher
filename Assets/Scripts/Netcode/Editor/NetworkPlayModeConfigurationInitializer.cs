namespace AnimarsCatcher.Networking.Editor
{
    using Unity.NetCode;
    using UnityEditor;

    /// <summary>
    /// 将 NetCode 编辑器播放模式同步到运行时配置桥接层
    /// </summary>
    [InitializeOnLoad]
    public static class NetworkPlayModeConfigurationInitializer
    {
        static NetworkPlayModeConfigurationInitializer()
        {
            Refresh();
        }

        [InitializeOnEnterPlayMode]
        private static void OnEnterPlayMode(EnterPlayModeOptions options)
        {
            Refresh();
        }

        private static void Refresh()
        {
            NetworkPlayModeConfiguration.ConfigureEditorPlayMode(
                ClientServerBootstrap.RequestedPlayType,
                ClientServerBootstrap.RequestedNumThinClients);
        }
    }
}
