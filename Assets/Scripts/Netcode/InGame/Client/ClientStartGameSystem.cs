using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using AnimarsCatcher.Mono.Global;
// using UnityEngine.SceneManagement; // 备用

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(RpcSystem))]
public partial struct ClientStartGameSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<NetworkId>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        bool hasStart = false;
        FixedString64Bytes sceneName = default;

        foreach (var (rpc, req, entity) in SystemAPI
                 .Query<RefRO<ClientStartGameRpc>, RefRO<ReceiveRpcCommandRequest>>()
                 .WithEntityAccess())
        {
            hasStart  = true;
            sceneName = rpc.ValueRO.SceneName;

            entityCommandBuffer.DestroyEntity(entity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();

        if (!hasStart)
            return;

        string sceneNameStr = sceneName.ToString();
        Debug.Log($"[ClientStartGameSystem] Received ClientStartGameRpc, loading scene '{sceneNameStr}' via GlobalLoadingUI.");

        // 标记对局开始状态
        var matchStateEntity = state.EntityManager.CreateEntity(typeof(ClientMatchStartState));
        state.EntityManager.SetComponentData(matchStateEntity, new ClientMatchStartState { Active = 1 });

        int localNetId = SystemAPI.GetSingleton<NetworkId>().Value;
        NetUIEventBridge.RaiseMatchStartedEvent(NetUIEventSource.ClientWorld, localNetId);

        // 通过全局 Loading UI 做异步加载 + 遮罩
        if (GlobalLoadingUI.Instance != null)
        {
            GlobalLoadingUI.Instance.StartLoadingAndTransition(sceneNameStr);
        }
        else
        {
            // 兜底：如果忘了在主菜单场景放 GlobalLoadingUI，就直接同步加载
            Debug.LogWarning("[ClientStartGameSystem] GlobalLoadingUI.Instance is null, fallback to direct LoadScene.");
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneNameStr);
            ClientCinematicState.ShouldRunIntro = true;
        }
    }
}
