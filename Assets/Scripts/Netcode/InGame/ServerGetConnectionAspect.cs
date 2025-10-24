using Unity.Entities;
using Unity.NetCode;

public struct PlayerSpawnedTag : IComponentData {}

// 连接实体的 Aspect（只处理：进局标记、去重、CommandTarget）
public readonly partial struct ServerGetConnectionAspect : IAspect
{
    public readonly Entity Self;

    readonly RefRO<NetworkId> _networkId;

    public int Id => _networkId.ValueRO.Value;

    public void EnsureInGame(ref SystemState state, ref EntityCommandBuffer entityCommandBuffer)
    {
        if (!state.EntityManager.HasComponent<NetworkStreamInGame>(Self))
        {
            entityCommandBuffer.AddComponent<NetworkStreamInGame>(Self);
        }
    }

    public bool HasSpawned(ref SystemState state)
    {
        return state.EntityManager.HasComponent<PlayerSpawnedTag>(Self);
    }

    public void MarkSpawned(ref EntityCommandBuffer entityCommandBuffer)
    {
        entityCommandBuffer.AddComponent<PlayerSpawnedTag>(Self);
    }

    public void SetCommandTarget(Entity character, ref SystemState state, ref EntityCommandBuffer entityCommandBuffer)
    {
        if (state.EntityManager.HasComponent<CommandTarget>(Self))
        {
            entityCommandBuffer.SetComponent(Self, new CommandTarget { targetEntity = character });
        }
        else
        {
            entityCommandBuffer.AddComponent(Self, new CommandTarget { targetEntity = character });
        }
    }
}