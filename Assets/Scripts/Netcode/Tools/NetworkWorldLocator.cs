namespace AnimarsCatcher.Networking
{
    using Unity.Entities;
    using Unity.NetCode;

    /// <summary>
    /// 提供 NetCode World 类型判断和实例查找
    /// </summary>
    public static class NetworkWorldLocator
    {
        /// <summary>
        /// 判断系统是否运行在 Client World
        /// </summary>
        /// <param name="state">系统状态</param>
        /// <returns>是否为客户端世界</returns>
        public static bool IsClient(ref SystemState state) => state.WorldUnmanaged.IsClient();
        /// <summary>
        /// 判断系统是否运行在 Server World
        /// </summary>
        /// <param name="state">系统状态</param>
        /// <returns>是否为服务器世界</returns>
        public static bool IsServer(ref SystemState state) => state.WorldUnmanaged.IsServer();
        /// <summary>
        /// 判断系统是否运行在 Thin Client World
        /// </summary>
        /// <param name="state">系统状态</param>
        /// <returns>是否为 Thin Client 世界</returns>
        public static bool IsThinClient(ref SystemState state) => state.WorldUnmanaged.IsThinClient();

        /// <summary>
        /// 获取适合调试日志使用的 World 类型标签
        /// </summary>
        /// <param name="state">系统状态</param>
        /// <returns>World 类型标签</returns>
        public static string GetWorldTag(ref SystemState state)
        {
            var isClient = IsClient(ref state);
            var isServer = IsServer(ref state);
            var isThinClient = IsThinClient(ref state);

            if (isServer && !isClient) return "[Server]";

            if (isClient && !isServer) return "[Client]";

            if (isThinClient) return "[ThinClient]";

            return "[Client & Server]"; // 本地模拟世界同时具备客户端和服务器标志
        }

        /// <summary>
        /// 查找当前进程中的游戏客户端 World
        /// </summary>
        /// <returns>客户端 World，未找到时返回 null</returns>
        public static World FindClientWorld()
        {
            foreach (var world in World.All)
            {
                if (world.Flags.HasFlag(WorldFlags.GameClient))
                {
                    return world;
                }
            }

            return null;
        }

        /// <summary>
        /// 查找当前进程中的游戏服务器 World
        /// </summary>
        /// <returns>服务器 World，未找到时返回 null</returns>
        public static World FindServerWorld()
        {
            foreach (var world in World.All)
            {
                if (world.Flags.HasFlag(WorldFlags.GameServer))
                {
                    return world;
                }
            }

            return null;
        }

    }
}
