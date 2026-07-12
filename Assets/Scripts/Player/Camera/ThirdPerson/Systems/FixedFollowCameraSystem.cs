using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.CharacterController;
using Unity.NetCode;

/// <summary>
/// 在客户端预测世界中按固定角度跟随受控角色
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
// 角色姿态确定后再计算相机，避免读取上一帧的 KCC 状态
[UpdateAfter(typeof(ThirdPersonCharacterVariableUpdateSystem))]
[UpdateAfter(typeof(ThirdPersonCharacterPhysicsUpdateSystem))]

[BurstCompile]
public partial struct FixedFollowCameraSystem : ISystem
{
    /// <summary>
    /// 声明固定相机运行所需的组件查询
    /// </summary>
    /// <param name="state">系统状态</param>
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<FixedCamera, FixedCameraControl, LocalTransform>() // 相机实体要有 LocalTransform
                .Build());
    }

    /// <summary>
    /// 根据预测角色姿态更新固定相机的位置和朝向
    /// </summary>
    /// <param name="state">系统状态</param>
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        float deltaTime = SystemAPI.Time.DeltaTime;

        foreach (var (config, control, cameraTransform, _)
                 in SystemAPI
                    .Query<RefRO<FixedCamera>, RefRO<FixedCameraControl>, RefRW<LocalTransform>>()
                    .WithEntityAccess())
        {
            var followed = control.ValueRO.FollowedCharacterEntity;
            if (followed == Entity.Null)
                continue;

            // 此系统运行在预测组，必须直接读取预测角色的 LocalTransform
            if (!SystemAPI.HasComponent<LocalTransform>(followed))
                continue;

            var targetLt = SystemAPI.GetComponent<LocalTransform>(followed);
            float3 targetPos = targetLt.Position;
            float3 up        = math.up();

            // 按固定偏航角和俯仰角构造相机朝向
            float3 planarForward =
                math.normalizesafe(MathUtilities.ProjectOnPlane(new float3(0, 0, 1), up));
            if (math.lengthsq(planarForward) < 1e-6f)
                planarForward = math.normalizesafe(
                    MathUtilities.ProjectOnPlane(new float3(1, 0, 0), up));

            quaternion baseRot    = quaternion.LookRotationSafe(planarForward, up);
            quaternion yawRot     = quaternion.AxisAngle(up, math.radians(config.ValueRO.YawDeg));
            quaternion yawApplied = math.mul(yawRot, baseRot);
            float3 right          = MathUtilities.GetRightFromRotation(yawApplied);
            quaternion pitchRot   = quaternion.AxisAngle(right, math.radians(config.ValueRO.PitchDeg));
            quaternion orientRot  = math.mul(pitchRot, yawApplied);

            float3 backDir = math.mul(orientRot, new float3(0, 0, -1));

            float3 desiredPos = targetPos
                                + backDir * config.ValueRO.Distance
                                + new float3(0, config.ValueRO.Height, 0);

            float3 lookAt      = targetPos + new float3(0, config.ValueRO.LookUpBias, 0);
            float3 forward     = math.normalizesafe(lookAt - desiredPos);
            quaternion cameraRotation = quaternion.LookRotationSafe(forward, up);

            cameraTransform.ValueRW.Position = desiredPos;
            cameraTransform.ValueRW.Rotation = cameraRotation;
            cameraTransform.ValueRW.Scale    = 1f;
        }
    }
}
