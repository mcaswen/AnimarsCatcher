using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using AnimarsCatcher.Mono.Global;

/// <summary>在 Client World 接收开局通知并切换到服务器指定场景</summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(RpcSystem))]
public partial struct ClientStartGameSystem : ISystem
{
    /// <summary>等待客户端连接完成后处理开局 RPC</summary>
    /// <param name="state">系统状态</param>
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<NetworkId>();
    }

    /// <summary>消费开局 RPC、建立客户端对局状态并启动场景过渡</summary>
    /// <param name="state">系统状态</param>
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

        string sceneNameString = sceneName.ToString();
        Debug.Log($"[ClientStartGameSystem] Received ClientStartGameRpc, loading scene '{sceneNameString}' via GlobalLoadingUI.");

        // 本地状态用于阻止场景就绪系统在开局通知前运行
        var matchStateEntity = state.EntityManager.CreateEntity(typeof(ClientMatchStartState));
        state.EntityManager.SetComponentData(matchStateEntity, new ClientMatchStartState { Active = 1 });

        int localNetworkId = SystemAPI.GetSingleton<NetworkId>().Value;
        NetworkUIEventBridge.RaiseMatchStartedEvent(NetworkUIEventSource.ClientWorld, localNetworkId);

        // 优先通过全局加载界面异步切场景并遮挡加载过程
        if (GlobalLoadingUI.Instance != null)
        {
            GlobalLoadingUI.Instance.StartLoadingAndTransition(sceneNameString);
        }
        else
        {
            // 加载界面缺失时同步切场景，保证协议仍能继续完成
            Debug.LogWarning("[ClientStartGameSystem] GlobalLoadingUI.Instance is null, fallback to direct LoadScene.");
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneNameString);
            ClientCinematicState.ShouldRunIntro = true;
        }
    }
}
