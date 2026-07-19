namespace AnimarsCatcher.Networking
{
    using System;
    using Unity.NetCode;
    using UnityEngine;

    /// <summary>
    /// 当前进程承担的网络运行角色
    /// </summary>
    public enum NetworkRunRole
    {
        Host,
        Client,
        DedicatedServer
    }

    /// <summary>
    /// 在启动早期检测并公开当前网络运行角色
    /// </summary>
    public static class NetworkRuntimeRole
    {
        /// <summary>
        /// 获取当前进程承担的网络运行角色
        /// </summary>
        public static NetworkRunRole Current { get; private set; } = NetworkRunRole.Host;

        /// <summary>
        /// 获取当前进程是否同时运行客户端和服务端
        /// </summary>
        public static bool IsHost => Current == NetworkRunRole.Host;

        /// <summary>
        /// 获取当前进程是否只运行客户端
        /// </summary>
        public static bool IsClient => Current == NetworkRunRole.Client;

        /// <summary>
        /// 获取当前进程是否只运行专用服务端
        /// </summary>
        public static bool IsDedicatedServer => Current == NetworkRunRole.DedicatedServer;

        /// <summary>
        /// 显式设置当前运行角色并记录来源
        /// </summary>
        /// <param name="role">目标网络角色</param>
        /// <param name="reason">设置角色的来源</param>
        public static void SetRole(NetworkRunRole role, string reason = null)
        {
            Current = role;
            Debug.Log($"[NetworkRuntimeRole] Set role = {role}" +
                      (string.IsNullOrEmpty(reason) ? "" : $" (from {reason})"));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void DetectRole()
        {
            if (NetworkPlayModeConfiguration.HasEditorOverride)
            {
                DetectEditorRole();
                return;
            }

            DetectCommandLineRole();
        }

        private static void DetectEditorRole()
        {
            switch (NetworkPlayModeConfiguration.PlayType)
            {
                case ClientServerBootstrap.PlayType.ClientAndServer:
                    SetRole(NetworkRunRole.Host, "Editor PlayMode ClientAndServer");
                    break;
                case ClientServerBootstrap.PlayType.Client:
                    SetRole(NetworkRunRole.Client, "Editor PlayMode Client");
                    break;
                case ClientServerBootstrap.PlayType.Server:
                    SetRole(NetworkRunRole.DedicatedServer, "Editor PlayMode Server");
                    break;
                default:
                    SetRole(NetworkRunRole.Host, "Editor PlayMode Unknown");
                    break;
            }
        }

        private static void DetectCommandLineRole()
        {
            string[] arguments = Environment.GetCommandLineArgs();

            if (HasArgument(arguments, "-dedicated") ||
                HasArgument(arguments, "-server") ||
                HasArgument(arguments, "-serverui"))
            {
                Current = NetworkRunRole.DedicatedServer;
            }
            else if (HasArgument(arguments, "-client"))
            {
                Current = NetworkRunRole.Client;
            }
            else if (HasArgument(arguments, "-host"))
            {
                Current = NetworkRunRole.Host;
            }
            else
            {
                Current = GetDefaultBuildRole();
            }

            Debug.Log($"[NetworkRuntimeRole] Launch as {Current}");
        }

        private static bool HasArgument(string[] arguments, string flag)
        {
            return Array.Exists(
                arguments,
                argument => string.Equals(
                    argument,
                    flag,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static NetworkRunRole GetDefaultBuildRole()
        {
            return ClientServerBootstrap.RequestedPlayType switch
            {
                ClientServerBootstrap.PlayType.Server => NetworkRunRole.DedicatedServer,
                ClientServerBootstrap.PlayType.Client => NetworkRunRole.Client,
                _ => NetworkRunRole.Client
            };
        }
    }
}
