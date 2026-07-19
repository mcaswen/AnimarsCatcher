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
    /// 保留正式资源应用链路以便联调权限和同步行为
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(RpcSystem))]
    public partial struct ServerDebugResourceToEventSystem : ISystem
    {
        private Entity _hubEntity;

        /// <summary>
        /// 确保事件 Hub 存在并消费全部资源变化 RPC
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            // 场景未提供 Hub 时创建运行时后备实体
            if (!entityManager.Exists(_hubEntity))
            {
                _hubEntity = Entity.Null;
                _hubEntity = entityManager.CreateEntity();

                entityManager.AddComponent<ResourceEventHubTag>(_hubEntity);

                entityManager.AddBuffer<FoodAmountChangedEvent>(_hubEntity);
                entityManager.AddBuffer<CrystalAmountChangedEvent>(_hubEntity);

                Debug.Log("[ServerDebugResourceToEventSystem] Created ResourceEventHub entity.");
            }

            var foodBuffer = entityManager.GetBuffer<FoodAmountChangedEvent>(_hubEntity);
            var crystalBuffer = entityManager.GetBuffer<CrystalAmountChangedEvent>(_hubEntity);

            // 将连接来源解析为玩家 NetworkId 后写入对应事件缓冲区
            foreach (var (rpc, request, rpcEntity) in SystemAPI
                         .Query<RefRO<ResourceChangedRpc>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                var connectionEntity = request.ValueRO.SourceConnection;

                if (!entityManager.HasComponent<NetworkId>(connectionEntity))
                {
                    Debug.LogWarning("[ServerDebugResourceToEventSystem] SourceConnection has no NetworkId.");
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                int networkId = entityManager.GetComponentData<NetworkId>(connectionEntity).Value;
                int amount = rpc.ValueRO.Amount;

                switch (rpc.ValueRO.Type)
                {
                    case ResourceItemKind.Food:
                        foodBuffer.Add(new FoodAmountChangedEvent
                        {
                            OwnerNetworkId = networkId,
                            Amount = amount
                        });

                        break;

                    case ResourceItemKind.Crystal:
                        crystalBuffer.Add(new CrystalAmountChangedEvent
                        {
                            OwnerNetworkId = networkId,
                            Amount = amount
                        });

                        break;
                }

                Debug.Log($"[ServerDebugResourceToEventSystem] Enqueued {rpc.ValueRO.Type} +{amount} for NetworkId={networkId}");

                entityCommandBuffer.DestroyEntity(rpcEntity);
            }

            entityCommandBuffer.Playback(entityManager);
        }
    }
}
