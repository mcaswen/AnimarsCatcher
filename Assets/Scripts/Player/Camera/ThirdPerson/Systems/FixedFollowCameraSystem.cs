using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.CharacterController;
using Unity.NetCode;

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
// 确保在 KCC 变量更新之后
[UpdateAfter(typeof(ThirdPersonCharacterVariableUpdateSystem))]
[UpdateAfter(typeof(ThirdPersonCharacterPhysicsUpdateSystem))]

[BurstCompile]
public partial struct FixedFollowCameraSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<FixedCamera, FixedCameraControl, LocalTransform>() // 相机实体要有 LocalTransform
                .Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        float deltaTime = SystemAPI.Time.DeltaTime;

        foreach (var (config, control, camTransform, camEntity)
                 in SystemAPI
                    .Query<RefRO<FixedCamera>, RefRO<FixedCameraControl>, RefRW<LocalTransform>>()
                    .WithEntityAccess())
        {
            var followed = control.ValueRO.FollowedCharacterEntity;
            if (followed == Entity.Null)
                continue;

            // 直接用“预测世界”的 LocalTransform
            if (!SystemAPI.HasComponent<LocalTransform>(followed))
                continue;

            var targetLt = SystemAPI.GetComponent<LocalTransform>(followed);
            float3 targetPos = targetLt.Position;
            float3 up        = math.up();

            // yaw + pitch 固定角度
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
            quaternion camRot  = quaternion.LookRotationSafe(forward, up);

            camTransform.ValueRW.Position = desiredPos;
            camTransform.ValueRW.Rotation = camRot;
            camTransform.ValueRW.Scale    = 1f;
        }
    }
}
