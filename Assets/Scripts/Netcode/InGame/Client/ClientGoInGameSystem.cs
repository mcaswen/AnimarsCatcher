namespace AnimarsCatcher.Networking
{
    using Unity.Entities;
    using Unity.NetCode;
    using UnityEngine.SceneManagement;

    /// <summary>
    /// 在编辑器游戏场景中自动完成客户端 InGame 调试握手
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct ClientGoInGameSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            if (!NetworkPlayModeConfiguration.HasEditorOverride)
            {
                state.Enabled = false;
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            // 场景限制防止调试握手介入正式大厅和菜单流程
            if (SceneManager.GetActiveScene().name != "SCN_GameLevel" ||
                !SystemAPI.TryGetSingletonEntity<NetworkId>(out Entity connection) ||
                SystemAPI.HasComponent<NetworkStreamInGame>(connection))
            {
                return;
            }

            Entity rpcEntity = state.EntityManager.CreateEntity();
            state.EntityManager.AddComponentData(rpcEntity, new GoInGameRequest());
            state.EntityManager.AddComponentData(
                rpcEntity,
                new SendRpcCommandRequest { TargetConnection = connection });

            state.EntityManager.AddComponent<NetworkStreamInGame>(connection);
            UnityEngine.Debug.Log(
                "[Client][Editor SCN_GameLevel] Auto sent GoInGameRequest and marked InGame locally");
        }
    }
}
