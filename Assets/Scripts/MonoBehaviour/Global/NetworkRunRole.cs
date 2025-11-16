using System;
using UnityEngine;
using Unity.NetCode;

namespace AnimarsCatcher.Mono.Global
{
    public enum NetworkRunRole
    {
        Host,   // Server + Client 同进程
        Client, // 纯客户端
        DedicatedServer // 服务端，暂未启用
    }

    public static class NetRuntimeRole
    {
        public static NetworkRunRole Current { get; private set; } = NetworkRunRole.Host;

        public static bool IsHost => Current == NetworkRunRole.Host;
        public static bool IsClient => Current == NetworkRunRole.Client;
        public static bool IsDedicatedServer => Current == NetworkRunRole.DedicatedServer;

        public static void SetRole(NetworkRunRole role, string reason = null)
        {
            Current = role;
            Debug.Log($"[NetRuntimeRole] Set role = {role}" +
                      (string.IsNullOrEmpty(reason) ? "" : $" (from {reason})"));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void DetectRole()
        {
            
#if UNITY_EDITOR
            // Editor中从 PlayMode Tools 的 PlayType 推断角色
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
            // 非 Editor 下从命令行参数推断角色
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
                // 默认Host
                Current = NetworkRunRole.Host;
            }

            Debug.Log($"[NetRuntimeRole] Launch as {Current}.");
#endif
        }
    }
}
