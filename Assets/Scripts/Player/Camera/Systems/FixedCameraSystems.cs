using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.CharacterController;

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FixedStepSimulationSystemGroup))]
[UpdateAfter(typeof(ThirdPersonPlayerVariableStepControlSystem))]
[UpdateAfter(typeof(ThirdPersonCharacterVariableUpdateSystem))]
[UpdateBefore(typeof(TransformSystemGroup))]
[BurstCompile]
public partial struct FixedFollowCameraSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<FixedCamera>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var job = new FixedCameraSimulationJob
        {
            DeltaTime = SystemAPI.Time.DeltaTime,
            LocalTransformLookup = SystemAPI.GetComponentLookup<LocalTransform>(false),
            ParentLookup = SystemAPI.GetComponentLookup<Parent>(true),
            PostTransformMatrixLookup = SystemAPI.GetComponentLookup<PostTransformMatrix>(true),
            CameraTargetLookup = SystemAPI.GetComponentLookup<CameraTarget>(true)
        };

        job.Schedule();
    }

    [BurstCompile]
    public partial struct FixedCameraSimulationJob : IJobEntity
    {
        public ComponentLookup<LocalTransform> LocalTransformLookup;

        [ReadOnly] public ComponentLookup<Parent> ParentLookup;
        [ReadOnly] public ComponentLookup<PostTransformMatrix> PostTransformMatrixLookup;
        [ReadOnly] public ComponentLookup<CameraTarget> CameraTargetLookup;
        public float DeltaTime;

        void Execute(Entity entity, ref FixedCamera cfg, in FixedCameraControl control)
        {
            if (control.FollowedCharacterEntity == Entity.Null)
                return;

            // 取被跟随目标的 仿真世界变换
            if (OrbitCameraUtilities.TryGetCameraTargetSimulationWorldTransform(
                    control.FollowedCharacterEntity,
                    ref LocalTransformLookup,
                    ref ParentLookup,
                    ref PostTransformMatrixLookup,
                    ref CameraTargetLookup,
                    out float4x4 targetWorld))
            {
                float3 targetUp  = targetWorld.Up();
                float3 targetPos = targetWorld.Translation();

                // 在 targetUp 切平面上选定一个参考前向作为 yaw = 0 的平面朝向
                float3 refForward = new float3(0, 0, 1);
                float3 planarFwd  = math.normalizesafe(MathUtilities.ProjectOnPlane(refForward, targetUp));
                if (math.lengthsq(planarFwd) < 1e-6f)
                {
                    refForward = new float3(1, 0, 0);
                    planarFwd = math.normalizesafe(MathUtilities.ProjectOnPlane(refForward, targetUp));
                } 

                // 拿到初始旋转
                quaternion baseRotation = quaternion.LookRotationSafe(planarFwd, targetUp);
                quaternion yawRotation = quaternion.AxisAngle(targetUp, math.radians(cfg.YawDeg));
                quaternion yawApplied = math.mul(yawRotation, baseRotation);

                float3 right = MathUtilities.GetRightFromRotation(yawApplied);
                quaternion pitchRotation = quaternion.AxisAngle(right, math.radians(cfg.PitchDeg));
                quaternion fixedRotation = math.mul(pitchRotation, yawApplied);

                float3 backDireciton = math.mul(fixedRotation, new float3(0, 0, -1));

                // 利用背向向量计算初始期望位置
                float3 desiredPosition = targetPos
                                + backDireciton * cfg.Distance
                                + new float3(0, cfg.Height, 0);

                // 利用阻尼从当前 LocalTransform 计算过渡到期望位置
                float3 currentPostion = desiredPosition;
                quaternion currentRotiton = fixedRotation;

                if (LocalTransformLookup.HasComponent(entity))
                {
                    var localTransform = LocalTransformLookup[entity];
                    currentPostion = localTransform.Position;
                    currentRotiton = localTransform.Rotation;
                }

                float damping = math.exp(-DeltaTime / math.max(0.0001f, cfg.Damping));
                float3 newPositon = math.lerp(desiredPosition, currentPostion, damping);

                // 加入高度偏移
                float3 lookAt = targetPos + new float3(0, cfg.LookUpBias, 0);
                float3 forward = math.normalizesafe(lookAt - newPositon, MathUtilities.GetForwardFromRotation(currentRotiton));
                quaternion newRotation = quaternion.LookRotationSafe(forward, targetUp);

                // 最后写回 LocalTransform
                LocalTransformLookup[entity] = LocalTransform.FromPositionRotation(newPositon, newRotation);
            }
        }
    }
}
