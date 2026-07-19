namespace AnimarsCatcher.Networking
{
    using Unity.NetCode;
    using Unity.Networking.Transport;

    /// <summary>
    /// 根据编辑器播放模式或运行时网络角色创建 NetCode World
    /// </summary>
    public class CustomBootstrap : ClientServerBootstrap
    {
        /// <summary>
        /// 创建当前进程需要的客户端、服务端和 Thin Client World
        /// </summary>
        /// <param name="defaultWorldName">Unity 提供的默认 World 名称</param>
        /// <returns>是否已完成自定义 World 初始化</returns>
        public override bool Initialize(string defaultWorldName)
        {
            DefaultConnectAddress = NetworkEndpoint.LoopbackIpv4;
            AutoConnectPort = 0;

            if (NetworkPlayModeConfiguration.HasEditorOverride)
            {
                return CreateEditorWorlds();
            }

            return CreateRuntimeWorlds();
        }

        private static bool CreateEditorWorlds()
        {
            switch (NetworkPlayModeConfiguration.PlayType)
            {
                case PlayType.ClientAndServer:
                    CreateServerWorld("Server World");
                    CreateClientWorld("Client World");
                    CreateThinClientWorlds();
                    return true;

                case PlayType.Client:
                    CreateClientWorld("Client World");
                    CreateThinClientWorlds();
                    return true;

                case PlayType.Server:
                    CreateServerWorld("Server World");
                    return true;

                default:
                    return false;
            }
        }

        private static void CreateThinClientWorlds()
        {
            for (int i = 0; i < NetworkPlayModeConfiguration.ThinClientCount; i++)
            {
                CreateThinClientWorld();
            }
        }

        private static bool CreateRuntimeWorlds()
        {
            switch (NetworkRuntimeRole.Current)
            {
                case NetworkRunRole.Host:
                    CreateServerWorld("Server World");
                    CreateClientWorld("Client World");
                    return true;

                case NetworkRunRole.Client:
                    CreateClientWorld("Client World");
                    return true;

                case NetworkRunRole.DedicatedServer:
                    CreateServerWorld("Server World");
                    return true;

                default:
                    return false;
            }
        }
    }
}
