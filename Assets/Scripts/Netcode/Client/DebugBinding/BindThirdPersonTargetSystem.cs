using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(GhostInputSystemGroup), OrderFirst = true)]
public partial struct BindThirdPersonTargetSystem : ISystem
{
    public void OnCreate(ref SystemState s)
    {
        s.RequireForUpdate<NetworkStreamInGame>();
        s.RequireForUpdate(SystemAPI.QueryBuilder()
            .WithAll<ThirdPersonPlayer, ThirdPersonPlayerInputs, PlayerTag>().Build());
    }

    public void OnUpdate(ref SystemState s)
    {
        var netWorkTime = SystemAPI.GetSingleton<NetworkTime>();
        if (!SystemAPI.TryGetSingletonEntity<NetworkStreamInGame>(out var connection)) return;

        var localId = SystemAPI.GetComponent<NetworkId>(connection).Value;
        var commandTargetRW = SystemAPI.GetComponentRW<CommandTarget>(connection);

        foreach (var (playerRW, ePlayer) in SystemAPI
                     .Query<RefRW<ThirdPersonPlayer>>()
                     .WithAll<ThirdPersonPlayerInputs, PlayerTag>()
                     .WithEntityAccess())
        {
            bool needBindCharacter = playerRW.ValueRO.ControlledCharacter == Entity.Null;

            if (needBindCharacter || commandTargetRW.ValueRO.targetEntity == Entity.Null)
            {
                Entity ownedCharacter = Entity.Null;

                // 查找本地拥有的 character
                foreach (var (owner, predicted, commandBuffer, characterEntity) in SystemAPI
                             .Query<RefRO<GhostOwner>, RefRO<PredictedGhost>, DynamicBuffer<ThirdPersonMoveCommand>>()
                             .WithAll<CharacterTag>()
                             .WithEntityAccess())
                {
                    if (owner.ValueRO.NetworkId == localId)
                    {
                        ownedCharacter = characterEntity;
                        break;
                    }
                }

                // 绑定 commandTarget 和 conrolledCharacter 到本地拥有的character
                if (ownedCharacter != Entity.Null)
                {
                    if (needBindCharacter)
                    {
                        var p = playerRW.ValueRO;
                        p.ControlledCharacter = ownedCharacter;
                        playerRW.ValueRW = p;
                    }

                    if (commandTargetRW.ValueRO.targetEntity == Entity.Null)
                        commandTargetRW.ValueRW.targetEntity = ownedCharacter;
                }
            }
        }
    }
}
