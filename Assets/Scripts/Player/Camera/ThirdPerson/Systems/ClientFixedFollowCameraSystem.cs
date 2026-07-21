namespace AnimarsCatcher.Player
{
    using Unity.Burst;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.Transforms;
    using Unity.CharacterController;

    /// <summary>
    /// 在客户端模拟阶段按固定角度跟随受控角色
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(TransformSystemGroup))]
    [BurstCompile]
    public partial struct ClientFixedFollowCameraSystem : ISystem
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
}
