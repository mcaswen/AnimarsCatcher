using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Collections;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(GhostInputSystemGroup), OrderFirst = true)]
public partial struct EnsureClientCommandTargetSystem : ISystem
{
    private bool _cameraIsBinded;
    private bool _characterIsBinded;

    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.TryGetSingletonEntity<NetworkStreamInGame>(out var connection))
            return;

        if (_cameraIsBinded && _characterIsBinded) return;

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        if (!state.EntityManager.HasComponent<CommandTarget>(connection))
            entityCommandBuffer.AddComponent(connection, new CommandTarget { targetEntity = Entity.Null });

        var commandTarget = SystemAPI.GetComponent<CommandTarget>(connection);
        if (commandTarget.targetEntity == Entity.Null)
        {
            var localNetId = SystemAPI.GetComponent<NetworkId>(connection).Value;

            foreach (var (owner, characterEntity)
                     in SystemAPI.Query<RefRO<GhostOwner>>().WithAll<CharacterTag, PredictedGhost>().WithEntityAccess())
            {
                if (owner.ValueRO.NetworkId == localNetId)
                {
                    entityCommandBuffer.SetComponent(connection, new CommandTarget { targetEntity = characterEntity });
                    if (SystemAPI.TryGetSingleton<ThirdPersonPlayerControl>(out var playerControl))
                    {
                        playerControl.ControlledCharacter = characterEntity;
                        SystemAPI.SetSingleton(playerControl);

                        _characterIsBinded = true;
                    }
                    break;
                }
            }

            foreach (var (owner, cameraEntity) in SystemAPI.Query<RefRO<GhostOwner>>()
                    .WithAll<PredictedGhost, MainEntityCamera>()
                    .WithEntityAccess())
            {
                if (owner.ValueRO.NetworkId == localNetId)
                {
                    if (SystemAPI.TryGetSingleton<ThirdPersonPlayerControl>(out var playerControl))
                    {
                        playerControl.ControlledCamera = cameraEntity;
                        SystemAPI.SetSingleton(playerControl);

                        _cameraIsBinded = true;
                    }
                    break;
                }
            }


        }

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
