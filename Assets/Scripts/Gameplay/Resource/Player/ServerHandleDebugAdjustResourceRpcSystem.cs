using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using AnimarsCatcher.Gameplay.Contracts;

namespace AnimarsCatcher.Gameplay
{

    /// <summary>
    /// 将客户端资源调试 RPC 转换为服务端资源事件
    /// 调试请求仍使用正式资源流程，便于联调权限和同步行为
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ServerHandleDebugAdjustResourceRpcSystem : ISystem
    {
        private Entity _hubEntity;

        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            // 场景未提供 Hub 时创建运行时后备 Entity
            if (!entityManager.Exists(_hubEntity))
            {
                _hubEntity = Entity.Null;
                _hubEntity = entityManager.CreateEntity();

                entityManager.AddComponent<PlayerResourceDeltaHubTag>(_hubEntity);

                entityManager.AddBuffer<FoodResourceDeltaEvent>(_hubEntity);
                entityManager.AddBuffer<CrystalResourceDeltaEvent>(_hubEntity);

                Debug.Log("[ServerHandleDebugAdjustResourceRpcSystem] Created PlayerResourceDeltaHub entity");
            }

            var foodBuffer = entityManager.GetBuffer<FoodResourceDeltaEvent>(_hubEntity);
            var crystalBuffer = entityManager.GetBuffer<CrystalResourceDeltaEvent>(_hubEntity);

            // 将连接来源解析为玩家 NetworkId 后写入对应事件缓冲区
            foreach (var (rpc, request, rpcEntity) in SystemAPI
                         .Query<RefRO<DebugAdjustResourceRpc>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                var connectionEntity = request.ValueRO.SourceConnection;

                if (!entityManager.HasComponent<NetworkId>(connectionEntity))
                {
                    Debug.LogWarning("[ServerHandleDebugAdjustResourceRpcSystem] SourceConnection has no NetworkId.");
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                int networkId = entityManager.GetComponentData<NetworkId>(connectionEntity).Value;
                int amount = rpc.ValueRO.Amount;

                switch (rpc.ValueRO.Kind)
                {
                    case ResourceItemKind.Food:
                        foodBuffer.Add(new FoodResourceDeltaEvent
                        {
                            OwnerNetworkId = networkId,
                            Amount = amount
                        });

                        break;

                    case ResourceItemKind.Crystal:
                        crystalBuffer.Add(new CrystalResourceDeltaEvent
                        {
                            OwnerNetworkId = networkId,
                            Amount = amount
                        });

                        break;
                }

                Debug.Log($"[ServerHandleDebugAdjustResourceRpcSystem] Enqueued {rpc.ValueRO.Kind} {amount:+#;-#;0} for NetworkId={networkId}");

                entityCommandBuffer.DestroyEntity(rpcEntity);
            }

            entityCommandBuffer.Playback(entityManager);
        }
    }
}
