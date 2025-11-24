using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using AnimarsCatcher.Mono.Global;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(RpcSystem))]
public partial struct ServerStartGameSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        // 只保证有自己的状态 singleton
        if (!state.EntityManager.CreateEntityQuery(typeof(ServerMatchStartState)).IsEmpty)
        {
            state.RequireForUpdate<ServerMatchStartState>();
            return;
        }

        var stateEntity = state.EntityManager.CreateEntity(typeof(ServerMatchStartState));
        state.EntityManager.SetComponentData(stateEntity, new ServerMatchStartState
        {
            SceneName           = default,
            MatchStartRequested = 0,
            ClientStartRpcSent  = 0,
            CharactersSpawned   = 0
        });

        state.RequireForUpdate<ServerMatchStartState>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        var matchStateRW = SystemAPI.GetSingletonRW<ServerMatchStartState>();

        bool hasStartRequestInThisFrame = false;
        FixedString64Bytes sceneNameFromRpc = default;

        // 处理 StartGameRpc
        foreach (var (startGameRpc, source, rpcEntity) in SystemAPI
                    .Query<RefRO<StartGameRpc>, RefRO<ReceiveRpcCommandRequest>>()
                    .WithEntityAccess())
        {
            hasStartRequestInThisFrame = true;
            sceneNameFromRpc = startGameRpc.ValueRO.SceneName;

            // 这里可以校验 source.ValueRO.SourceConnection 是不是 host
            entityCommandBuffer.DestroyEntity(rpcEntity);
        }

        if (hasStartRequestInThisFrame)
        {
            matchStateRW.ValueRW.SceneName           = sceneNameFromRpc;
            matchStateRW.ValueRW.MatchStartRequested = 1;
            matchStateRW.ValueRW.ClientStartRpcSent  = 0;
            matchStateRW.ValueRW.CharactersSpawned   = 0;

            UnityEngine.Debug.Log($"[Server] Match start requested, scene = '{sceneNameFromRpc.ToString()}'.");
        }

        // 如果还没人请求开始，直接退出
        if (matchStateRW.ValueRO.MatchStartRequested == 0)
        {
            entityCommandBuffer.Playback(state.EntityManager);
            entityCommandBuffer.Dispose();
            return;
        }

        var sceneName = matchStateRW.ValueRO.SceneName;

        // 处理 ClientStartRpcSent
        if (matchStateRW.ValueRO.ClientStartRpcSent == 0)
        {
            foreach (var (networkId, connectionEntity) in SystemAPI
                        .Query<RefRO<NetworkId>>()
                        .WithEntityAccess())
            {
                var rpcEntity = entityCommandBuffer.CreateEntity();
                entityCommandBuffer.AddComponent(rpcEntity, new ClientStartGameRpc
                {
                    SceneName = sceneName
                });
                entityCommandBuffer.AddComponent(rpcEntity, new SendRpcCommandRequest
                {
                    TargetConnection = connectionEntity
                });

                UnityEngine.Debug.Log($"[Server] Send ClientStartGameRpc to connection {networkId.ValueRO.Value}.");
            }

            matchStateRW.ValueRW.ClientStartRpcSent = 1;
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }

}
