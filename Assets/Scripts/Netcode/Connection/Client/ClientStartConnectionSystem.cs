namespace AnimarsCatcher.Networking
{
    using Unity.Burst;
    using Unity.Entities;
    using Unity.NetCode;
    using Unity.Networking.Transport;
    using UnityEngine.SceneManagement;

    /// <summary>
    /// 在编辑器游戏场景中自动创建本机客户端连接请求
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct ClientStartConnectionSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            if (!NetworkPlayModeConfiguration.HasEditorOverride ||
                SceneManager.GetActiveScene().name != "SCN_GameLevel")
            {
                state.Enabled = false;
                return;
            }

            if (!AlreadyConnectedOrConnecting(ref state))
            {
                var endPoint = NetworkEndpoint.LoopbackIpv4.WithPort(NetworkPorts.Game);
                Entity entity = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(
                    entity,
                    new NetworkStreamRequestConnect { Endpoint = endPoint });
                UnityEngine.Debug.Log("[Client] Connect Request Sent");
            }

            state.Enabled = false;
        }

        // 同时检查连接、连接请求和握手状态，防止创建重复连接 Entity
        private bool AlreadyConnectedOrConnecting(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<NetworkId>())
            {
                return true;
            }

            if (!SystemAPI.QueryBuilder()
                    .WithAll<NetworkStreamRequestConnect>()
                    .Build()
                    .IsEmpty)
            {
                return true;
            }

            return !SystemAPI.QueryBuilder()
                .WithAll<NetworkStreamConnection>()
                .Build()
                .IsEmpty;
        }
    }
}
