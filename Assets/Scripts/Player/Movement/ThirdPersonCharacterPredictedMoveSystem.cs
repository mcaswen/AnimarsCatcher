using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using Unity.Mathematics;
using Unity.CharacterController;
using System.Diagnostics;
public struct NetCodeMoveUpdateContext
{
    public float DeltaTime;
    public NetworkTick Tick;
}

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
[UpdateBefore(typeof(KinematicCharacterPhysicsUpdateGroup))]
public partial struct CharacterPredictedMoveSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(SystemAPI.QueryBuilder()
            .WithAll<PredictedGhost, SimpleCharacterControl, LocalTransform, InputCommand>()
            .Build());
    }

    public void OnUpdate(ref SystemState state)
    {
        var netWorkTime = SystemAPI.GetSingleton<NetworkTime>();

        foreach (var (controlRW, inputCommandBuffer) in SystemAPI
                 .Query<RefRW<SimpleCharacterControl>, DynamicBuffer<InputCommand>>()
                 .WithAll<CharacterTag, PredictedGhost>())
        {
            if (!inputCommandBuffer.GetDataAtTick(netWorkTime.ServerTick, out InputCommand command)) 
            {
                var controlRO = controlRW.ValueRO;
                UnityEngine.Debug.Log("[PredictedMoveSystem] {state.EntityManager.World} No Data At Tick! Control - MoveVector: {" + controlRO.MoveVector + "}, tick = " + netWorkTime.ServerTick);
                continue;
            }

            var control = controlRW.ValueRO;
            control.MoveVector = command.Move;  // 在 ThirdPersonMoveCommand 的计算与绑定中已是世界平面向量
            controlRW.ValueRW = control;
        
            UnityEngine.Debug.Log($"[PredictedMoveSystem] [{state.EntityManager.World}] Control - MoveVector: {control.MoveVector}, tick = " + netWorkTime.ServerTick);

            // 之后的control的相关计算由 ThirdPersonCharacterSystems 完成

        }
    }

}