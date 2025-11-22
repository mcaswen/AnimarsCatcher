using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using AnimarsCatcher.Mono.Global;


[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(RpcSystem))]
public partial struct ServerDebugResourceToEventSystem : ISystem
{
    private Entity _hubEntity;
    
    public void OnUpdate(ref SystemState state)
    {
        var entityManager = state.EntityManager;
        var entityCommanedBuffer = new EntityCommandBuffer(Allocator.Temp);
        
        // 创建事件 HUB 实体
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

        // 处理所有 ResourceChangedRpc
        foreach (var (rpc, request, rpcEntity) in SystemAPI
                     .Query<RefRO<ResourceChangedRpc>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            var connectionEntity = request.ValueRO.SourceConnection;

            if (!entityManager.HasComponent<NetworkId>(connectionEntity))
            {
                Debug.LogWarning("[ServerDebugResourceToEventSystem] SourceConnection has no NetworkId.");
                entityCommanedBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            int networkId = entityManager.GetComponentData<NetworkId>(connectionEntity).Value;
            int amount = rpc.ValueRO.Amount;

            switch (rpc.ValueRO.Type)
            {
                case ResourceType.Food:
                    foodBuffer.Add(new FoodAmountChangedEvent
                    {
                        OwnerNetworkId = networkId,
                        Amount = amount
                    });

                    break;

                case ResourceType.Crystal:
                    crystalBuffer.Add(new CrystalAmountChangedEvent
                    {
                        OwnerNetworkId = networkId,
                        Amount = amount
                    });

                    break;
            }

            Debug.Log($"[ServerDebugResourceToEventSystem] Enqueued {rpc.ValueRO.Type} +{amount} for NetworkId={networkId}");

            entityCommanedBuffer.DestroyEntity(rpcEntity);
        }

        entityCommanedBuffer.Playback(entityManager);
    }
}
