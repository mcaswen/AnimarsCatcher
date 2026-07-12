using System;
using UnityEngine;
using Unity.NetCode;

namespace AnimarsCatcher.Mono.Global
{
    /// <summary>
    /// 当前进程承担的网络运行角色
    /// </summary>
    public enum NetworkRunRole
    {
        Host,   // Server + Client 同进程
        Client, // 纯客户端
        DedicatedServer // 服务端，暂未启用
    }

    /// <summary>
    /// 在启动早期检测并公开当前网络运行角色
    /// </summary>
    public static class NetRuntimeRole
    {
        public static NetworkRunRole Current { get; private set; } = NetworkRunRole.Host;

        public static bool IsHost => Current == NetworkRunRole.Host;
        public static bool IsClient => Current == NetworkRunRole.Client;
        public static bool IsDedicatedServer => Current == NetworkRunRole.DedicatedServer;

        /// <summary>
        /// 显式设置当前运行角色并记录来源
        /// </summary>
        public static void SetRole(NetworkRunRole role, string reason = null)
        {
            Current = role;
            Debug.Log($"[NetRuntimeRole] Set role = {role}" +
                      (string.IsNullOrEmpty(reason) ? "" : $" (from {reason})"));
        }

        // 编辑器依据 NetCode PlayMode 工具判断 构建版本依据命令行参数判断
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void DetectRole()
        {
            
#if UNITY_EDITOR
            // Editor 中从 PlayMode Tools 的 PlayType 推断角色
            switch (ClientServerBootstrap.RequestedPlayType)
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
                    SetRole(NetworkRunRole.Host, "Editor PlayMode Unknown -> default Host");
                    break;
            }
#else
            // 非 Editor 环境从命令行参数推断角色
            var args = Environment.GetCommandLineArgs();

            bool has(string flag)
                => Array.Exists(args, a =>
                    string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

            if (has("-dedicated"))
            {
                Current = NetworkRunRole.DedicatedServer;
            }
            else if (has("-client"))
            {
                Current = NetworkRunRole.Client;
            }
            else if (has("-host"))
            {
                Current = NetworkRunRole.Host;
            }
            else
            {
                // 无显式参数时按普通客户端启动
                Current = NetworkRunRole.Client;
            }

            Debug.Log($"[NetRuntimeRole] Launch as {Current}.");
#endif
        }
    }
}
