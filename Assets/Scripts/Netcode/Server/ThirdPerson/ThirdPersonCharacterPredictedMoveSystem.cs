using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using Unity.CharacterController;
public struct NetCodeMoveUpdateContext
{
    public float DeltaTime;
    public NetworkTick Tick;
}

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(KinematicCharacterPhysicsUpdateGroup))]
[UpdateBefore(typeof(ThirdPersonCharacterPhysicsUpdateSystem))]
public partial struct CharacterPredictedMoveSystem : ISystem
{
    public void OnCreate(ref SystemState s)
    {
        s.RequireForUpdate(SystemAPI.QueryBuilder()
            .WithAll<PredictedGhost, ThirdPersonCharacterControl, LocalTransform, ThirdPersonMoveCommand>()
            .Build());
    }

    public void OnUpdate(ref SystemState s)
    {
        var netWorkTime = SystemAPI.GetSingleton<NetworkTime>();

        foreach (var (predictedGhost, controlRW, moveCommandBuffer) in SystemAPI
                 .Query<RefRO<PredictedGhost>, RefRW<ThirdPersonCharacterControl>, DynamicBuffer<ThirdPersonMoveCommand>>()
                 .WithAll<CharacterTag>())
        {
            if (!predictedGhost.ValueRO.ShouldPredict(netWorkTime.ServerTick) ||
                !moveCommandBuffer.GetDataAtTick(netWorkTime.ServerTick, out ThirdPersonMoveCommand command))
                continue;

            var control = controlRW.ValueRO;
            control.MoveVector = command.Move;   // 在 ThirdPersonMoveCommand 的计算与绑定中已是世界平面向量
            control.Jump = command.Jump;
            controlRW.ValueRW = control;

            // 之后的control的相关计算由 ThirdPersonCharacterSystems 完成

        }
    }

}