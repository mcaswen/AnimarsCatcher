using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using AnimarsCatcher.Mono.Global;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(RpcSystem))] // 收完 ClientStartGameRpc 之后
public partial struct ClientSetInGameWhenReadySystem : ISystem
{
    private bool   _hasSent;          // 已经发过 SetInGameRpc
    private bool   _sceneReadyOnce;   // 是否已经至少 ready 过一次
    private double _sceneReadyTime;   // 第一次 ready 的时间戳（秒）
    

    public void OnCreate(ref SystemState state)
    {
        // 必须有连接、必须有 GhostCollection、必须对局已开始 才跑
        state.RequireForUpdate<NetworkId>();
        state.RequireForUpdate<GhostCollection>();
        state.RequireForUpdate<ClientMatchStartState>();

        _hasSent         = false;
        _sceneReadyOnce  = false;
        _sceneReadyTime  = 0.0;
    }

    public void OnUpdate(ref SystemState state)
    {
        if (_hasSent)
            return;

        var entityManager = state.EntityManager;
        double elapsed    = SystemAPI.Time.ElapsedTime;

        // 1. 检查 GhostCollection 是否已经包含关键 prefab（说明 Game 场景 SubScene 已经加载）
        Entity ghostCollectionEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
        DynamicBuffer<GhostCollectionPrefab> prefabs =
            SystemAPI.GetBuffer<GhostCollectionPrefab>(ghostCollectionEntity);

        bool readyNow = IsClientSceneReady(ref state, prefabs);

        if (!readyNow)
        {
            // 如果中途又不 ready 了，就重置标记
            _sceneReadyOnce = false;
            return;
        }

        // 第一次从“不 ready”变成“ready”
        if (!_sceneReadyOnce)
        {
            _sceneReadyOnce = true;
            _sceneReadyTime = elapsed;
            Debug.Log("[ClientSetInGameWhenReadySystem] Scene marked ready, waiting extra 2s before entering InGame.");
            return;
        }

        // ready 但等待时间不足 2 秒，继续等
        const double extraDelaySeconds = 3.0;
        if (elapsed - _sceneReadyTime < extraDelaySeconds)
            return;

        // 2. 拿到本地这条连接的实体
        if (!SystemAPI.TryGetSingletonEntity<NetworkId>(out var connectionEntity))
            return;

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        // 3. 发送 SetInGameRpc 给服务器
        Entity rpcEntity = entityCommandBuffer.CreateEntity();
        entityCommandBuffer.AddComponent(rpcEntity, new SetInGameRpc());
        entityCommandBuffer.AddComponent(rpcEntity, new SendRpcCommandRequest
        {
            TargetConnection = connectionEntity
        });

        // 4. 本地也加 NetworkStreamInGame（让客户端输入/本地系统开始跑）
        if (!entityManager.HasComponent<NetworkStreamInGame>(connectionEntity))
        {
            entityCommandBuffer.AddComponent<NetworkStreamInGame>(connectionEntity);
            Debug.Log("[ClientSetInGameWhenReadySystem] Extra 2s passed, mark InGame and send SetInGameRpc.");
        }

        entityCommandBuffer.Playback(entityManager);
        entityCommandBuffer.Dispose();

        _hasSent = true;

        // 真正意义上的“对局开始”也可以在这里再通知一次 UI（可选）
        int localNetId = SystemAPI.GetSingleton<NetworkId>().Value;
        NetUIEventBridge.RaiseMatchStartedEvent(NetUIEventSource.ClientWorld, localNetId);
    }

    private bool IsClientSceneReady(ref SystemState state, DynamicBuffer<GhostCollectionPrefab> prefabs)
    {
        var entityManager = state.EntityManager;

        if (prefabs.Length == 0)
        {
            // Debug.Log("[ClientSetInGameWhenReadySystem] GhostCollectionPrefab buffer is empty.");
            return false;
        }

        bool hasCharacter = false;

        for (int i = 0; i < prefabs.Length; i++)
        {
            var entry        = prefabs[i];
            var prefabEntity = entry.GhostPrefab; // 或 entry.Prefab，看你版本

            if (prefabEntity == Entity.Null || !entityManager.Exists(prefabEntity))
            {
                Debug.LogError($"[ClientSetInGameWhenReadySystem] Prefab[{i}] is invalid: {prefabEntity}, Exists={entityManager.Exists(prefabEntity)}");
                // 只要有一个无效，就认为场景还没 ready，继续等
                return false;
            }

            string name = entityManager.GetName(prefabEntity);
            if (!hasCharacter && !string.IsNullOrEmpty(name) && name.Contains("Robot"))
                hasCharacter = true;
        }

        if (!hasCharacter)
        {
            Debug.Log("[ClientSetInGameWhenReadySystem] GhostCollection ready but no 'Robot' prefab found yet.");
            return false;
        }

        return hasCharacter;
    }
}
