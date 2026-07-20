namespace AnimarsCatcher.Networking
{
    using Unity.Burst;
    using Unity.Entities;
    using Unity.NetCode;
    using Unity.Networking.Transport;
    using UnityEngine.SceneManagement;

    /// <summary>
    /// 根据编辑器场景或运行时启动参数创建服务端监听请求
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct ServerStartListenSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            if (NetworkPlayModeConfiguration.HasEditorOverride)
            {
                CreateEditorListenRequest(ref state);
                state.Enabled = false;
                return;
            }

            CreateRuntimeListenRequest(ref state);
            state.Enabled = false;
        }

        private void CreateEditorListenRequest(ref SystemState state)
        {
            if (SceneManager.GetActiveScene().name != "SCN_GameLevel" ||
                HasListenRequest(ref state))
            {
                return;
            }

            CreateListenRequest(
                ref state,
                NetworkEndpoint.AnyIpv4.WithPort(NetworkPorts.Game),
                "ServerListenRequest (Editor SCN_GameLevel Auto)");
        }

        private void CreateRuntimeListenRequest(ref SystemState state)
        {
            bool shouldListen =
                CommandLineArguments.HasArgument("-server") ||
                CommandLineArguments.HasArgument("-serverui") ||
                CommandLineArguments.HasArgument("-dedicated");

            if (!shouldListen || HasListenRequest(ref state))
            {
                return;
            }

            // AnyIpv4 允许局域网和远端客户端访问服务端端口
            CreateListenRequest(
                ref state,
                NetworkEndpoint.AnyIpv4.WithPort(NetworkPorts.Game),
                "ServerListenRequest (Runtime)");
        }

        private bool HasListenRequest(ref SystemState state)
        {
            return !SystemAPI.QueryBuilder()
                .WithAll<NetworkStreamRequestListen>()
                .Build()
                .IsEmpty;
        }

        private static void CreateListenRequest(
            ref SystemState state,
            NetworkEndpoint endPoint,
            string entityName)
        {
            Entity requestEntity = state.EntityManager.CreateEntity();
            state.EntityManager.SetName(requestEntity, entityName);
            state.EntityManager.AddComponentData(
                requestEntity,
                new NetworkStreamRequestListen { Endpoint = endPoint });
        }
    }
}
