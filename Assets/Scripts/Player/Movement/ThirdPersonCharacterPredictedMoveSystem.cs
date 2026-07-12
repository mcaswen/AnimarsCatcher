using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using Unity.Mathematics;
using Unity.CharacterController;
using System.Diagnostics;
/// <summary>
/// 保存预测移动更新所需的时间信息
/// </summary>
public struct NetCodeMoveUpdateContext
{
    public float DeltaTime;
    public NetworkTick Tick;
}

/// <summary>
/// 在每个预测 Tick 将网络输入命令写入第三人称角色控制组件
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
[UpdateBefore(typeof(KinematicCharacterPhysicsUpdateGroup))]
public partial struct ThirdPersonCharacterPredictedMoveSystem : ISystem
{
    /// <summary>
    /// 等待预测角色和输入命令缓冲可用
    /// </summary>
    /// <param name="state">系统状态</param>
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(SystemAPI.QueryBuilder()
            .WithAll<PredictedGhost, ThirdPersonCharacterControl, LocalTransform, InputCommand>()
            .Build());
    }

    /// <summary>
    /// 读取当前服务器 Tick 对应的命令并更新预测移动向量
    /// </summary>
    /// <param name="state">系统状态</param>
    public void OnUpdate(ref SystemState state)
    {
        var networkTime = SystemAPI.GetSingleton<NetworkTime>();

        foreach (var (controlRW, inputCommandBuffer) in SystemAPI
                 .Query<RefRW<ThirdPersonCharacterControl>, DynamicBuffer<InputCommand>>()
                 .WithAll<CharacterTag, PredictedGhost>())
        {
            if (!inputCommandBuffer.GetDataAtTick(networkTime.ServerTick, out InputCommand command))
            {
                continue;
            }

            var control = controlRW.ValueRO;
            control.MoveVector = command.Move;  // 在 ThirdPersonMoveCommand 的计算与绑定中已是世界平面向量
            controlRW.ValueRW = control;
        
            // 此系统只绑定输入，速度和碰撞由后续 KCC 系统计算

        }
    }

}
