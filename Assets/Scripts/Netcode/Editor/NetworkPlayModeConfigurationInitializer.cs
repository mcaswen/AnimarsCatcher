namespace AnimarsCatcher.Networking.Editor
{
    using System;
    using Unity.NetCode;
    using UnityEditor;
    using UnityEngine;

    /// <summary>
    /// 将 NetCode 编辑器播放模式同步到运行时配置
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
            if (Application.isBatchMode && IsBenchmarkServerOnlyRequested())
            {
                NetworkPlayModeConfiguration.ConfigureEditorPlayMode(
                    ClientServerBootstrap.PlayType.Server,
                    0);
                return;
            }

            NetworkPlayModeConfiguration.ConfigureEditorPlayMode(
                ClientServerBootstrap.RequestedPlayType,
                ClientServerBootstrap.RequestedNumThinClients);
        }

        private static bool IsBenchmarkServerOnlyRequested()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            return Array.Exists(
                arguments,
                argument =>
                    string.Equals(
                        argument,
                        "-benchmark-server-only",
                        StringComparison.OrdinalIgnoreCase));
        }
    }
}
