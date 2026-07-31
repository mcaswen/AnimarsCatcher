namespace AnimarsCatcher.Networking
{
    using AnimarsCatcher.Gameplay.Contracts;
    using Unity.Entities;
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
                    CreateConfiguredServerWorld("Server World");
                    CreateConfiguredClientWorld("Client World");
                    CreateThinClientWorlds();
                    return true;

                case PlayType.Client:
                    CreateConfiguredClientWorld("Client World");
                    CreateThinClientWorlds();
                    return true;

                case PlayType.Server:
                    CreateConfiguredServerWorld("Server World");
                    return true;

                default:
                    return false;
            }
        }

        private static void CreateThinClientWorlds()
        {
            for (int i = 0; i < NetworkPlayModeConfiguration.ThinClientCount; i++)
            {
                World world = CreateThinClientWorld();
                ConfigureMovementBackend(world);
            }
        }

        private static bool CreateRuntimeWorlds()
        {
            switch (NetworkRuntimeRole.Current)
            {
                case NetworkRunRole.Host:
                    CreateConfiguredServerWorld("Server World");
                    CreateConfiguredClientWorld("Client World");
                    return true;

                case NetworkRunRole.Client:
                    CreateConfiguredClientWorld("Client World");
                    return true;

                case NetworkRunRole.DedicatedServer:
                    CreateConfiguredServerWorld("Server World");
                    return true;

                default:
                    return false;
            }
        }

        private static World CreateConfiguredServerWorld(string name)
        {
            World world = CreateServerWorld(name);
            ConfigureMovementBackend(world);
            return world;
        }

        private static World CreateConfiguredClientWorld(string name)
        {
            World world = CreateClientWorld(name);
            ConfigureMovementBackend(world);
            return world;
        }

        private static void ConfigureMovementBackend(World world)
        {
            AniMovementBackendWorldUtility.ConfigureWorld(
                world,
                AniMovementBackendLaunchConfiguration.Current);
        }
    }
}
