using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.SceneManagement;
using AnimarsCatcher.Mono.Global;

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

        if (!hasStart)
            return;

        string sceneNameStr = sceneName.ToString();
        Debug.Log($"[ClientStartGameSystem] Received ClientStartGameRpc, loading scene '{sceneNameStr}'.");

        // 标记本地连接 InGame（Client 侧）
        if (SystemAPI.TryGetSingletonEntity<NetworkId>(out var connectionEntity))
        {
            if (!state.EntityManager.HasComponent<NetworkStreamInGame>(connectionEntity))
            {
                state.EntityManager.AddComponent<NetworkStreamInGame>(connectionEntity);
                Debug.Log("[ClientStartGameSystem] Mark local connection as InGame.");
            }
        }

        int localNetId = SystemAPI.GetSingleton<NetworkId>().Value;

        // 通知 UI：对局开始
        NetUIEventBridge.RaiseMatchStartedEvent(NetUIEventSource.ClientWorld, localNetId);

        SceneManager.LoadScene(sceneNameStr);
    }
}
