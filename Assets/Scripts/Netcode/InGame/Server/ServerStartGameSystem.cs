namespace AnimarsCatcher.Networking
{
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.NetCode;
    using Unity.Transforms;

    /// <summary>
    /// 在 Server World 接收开局请求并向所有连接广播权威场景
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(RpcSystem))]
    public partial struct ServerStartGameSystem : ISystem
    {
        /// <summary>
        /// 创建唯一的服务器开局状态单例
        /// </summary>
        /// <param name="state">系统状态</param>
        public void OnCreate(ref SystemState state)
        {
            // 状态单例可能由场景烘焙或热重载保留，创建前必须去重
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

        /// <summary>
        /// 消费 StartGameRpc 并向当前所有连接广播开局通知
        /// </summary>
        /// <param name="state">系统状态</param>
        public void OnUpdate(ref SystemState state)
        {
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            var matchStateRW = SystemAPI.GetSingletonRW<ServerMatchStartState>();

            bool hasStartRequestInThisFrame = false;
            FixedString64Bytes sceneNameFromRpc = default;

            // 当前实现接受任一连接的请求，接入权限收紧时应在此校验 SourceConnection
            foreach (var (startGameRpc, source, rpcEntity) in SystemAPI
                        .Query<RefRO<StartGameRpc>, RefRO<ReceiveRpcCommandRequest>>()
                        .WithEntityAccess())
            {
                hasStartRequestInThisFrame = true;
                sceneNameFromRpc = startGameRpc.ValueRO.SceneName;

                entityCommandBuffer.DestroyEntity(rpcEntity);
            }

            if (hasStartRequestInThisFrame)
            {
                // 新请求会重置后续阶段，确保广播和角色创建只对应最新场景
                matchStateRW.ValueRW.SceneName           = sceneNameFromRpc;
                matchStateRW.ValueRW.MatchStartRequested = 1;
                matchStateRW.ValueRW.ClientStartRpcSent  = 0;
                matchStateRW.ValueRW.CharactersSpawned   = 0;

                UnityEngine.Debug.Log($"[Server] Match start requested, scene = '{sceneNameFromRpc.ToString()}'.");
            }

            // 未收到开局请求前不广播任何场景状态
            if (matchStateRW.ValueRO.MatchStartRequested == 0)
            {
                entityCommandBuffer.Playback(state.EntityManager);
                entityCommandBuffer.Dispose();
                return;
            }

            var sceneName = matchStateRW.ValueRO.SceneName;

            // 广播只执行一次，后续由客户端就绪 RPC 推进角色创建阶段
            if (matchStateRW.ValueRO.ClientStartRpcSent == 0)
            {
                // 逐连接定向发送，避免依赖广播 RPC 的连接过滤语义
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
}
