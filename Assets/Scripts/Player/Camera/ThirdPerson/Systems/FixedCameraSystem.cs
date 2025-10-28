using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.CharacterController;
using System;

[UpdateInGroup(typeof(PresentationSystemGroup))]
[UpdateAfter(typeof(FixedStepSimulationSystemGroup))]
// [UpdateAfter(typeof(ThirdPersonPlayerVariableStepControlSystem))]
// [UpdateAfter(typeof(ThirdPersonCharacterVariableUpdateSystem))]
[UpdateAfter(typeof(TransformSystemGroup))]
[BurstCompile]
public partial struct FixedFollowCameraSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(SystemAPI.QueryBuilder().WithAll<FixedCamera, FixedCameraControl>().Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var job = new FixedCameraSimulationJob
        {
            DeltaTime = SystemAPI.Time.DeltaTime,
            LocalToWorldLookup = SystemAPI.GetComponentLookup<LocalToWorld>(false),
            CameraTargetLookup = SystemAPI.GetComponentLookup<CameraTarget>(true)
        };

        job.Schedule();
    }

    [BurstCompile]
    public partial struct FixedCameraSimulationJob : IJobEntity
    {
        public ComponentLookup<LocalToWorld> LocalToWorldLookup; 
        [ReadOnly] public ComponentLookup<CameraTarget> CameraTargetLookup;
        public float DeltaTime;

        void Execute(Entity camera, ref FixedCamera cfg, in FixedCameraControl control)
        {
            if (control.FollowedCharacterEntity == Entity.Null)
                return;

            // 取被跟随目标的 仿真世界变换
            if (!OrbitCameraUtilities.TryGetCameraTargetInterpolatedWorldTransform(
                control.FollowedCharacterEntity, ref LocalToWorldLookup, ref CameraTargetLookup, out LocalToWorld target))
                return;
            
            float3 targetPos = target.Position;
            float3 up = math.up();

            // 在 targetUp 切平面上选定一个参考前向作为 yaw = 0 的平面朝向
            float3 planarFwd = math.normalizesafe(MathUtilities.ProjectOnPlane(new float3(0,0,1), up));
            if (math.lengthsq(planarFwd) < 1e-6f)
                planarFwd = math.normalizesafe(MathUtilities.ProjectOnPlane(new float3(1, 0, 0), up));

            
            // 计算期望旋转
            quaternion baseRot = quaternion.LookRotationSafe(planarFwd, up);
            quaternion yawRot = quaternion.AxisAngle(up, math.radians(cfg.YawDeg));
            quaternion yawApplied = math.mul(yawRot, baseRot);
            float3 right = MathUtilities.GetRightFromRotation(yawApplied);
            quaternion pitchRot = quaternion.AxisAngle(right, math.radians(cfg.PitchDeg));
            quaternion desiredRot = math.mul(pitchRot, yawApplied);


            float3 back = math.mul(desiredRot, new float3(0,0,-1));

            // 利用背向向量计算初始期望位置
            float3 desiredPos = targetPos + back * cfg.Distance + new float3(0, cfg.Height, 0);

            // 读取相机当前 localToWorld 用于平滑；首次则直接贴近目标
            float3 currPos; quaternion currRot;
            bool hasCurr = LocalToWorldLookup.TryGetComponent(camera, out LocalToWorld currLtW);
            if (!hasCurr)
            {
                currPos = desiredPos;
                currRot = desiredRot;
            }
            else
            {
                currPos = currLtW.Position;
                currRot = currLtW.Rotation;
            }

            // snap阈值
            float snapDist = math.max(0.01f, cfg.SnapDistance);
            float snapCos  = math.cos(math.radians(math.max(1f, cfg.SnapAngleDeg)));
            bool snapP = math.lengthsq(desiredPos - currPos) > snapDist * snapDist;
            bool snapR = math.abs(math.dot(currRot.value, desiredRot.value)) < snapCos;

            // 应用阻尼平滑
            float k = math.exp(-DeltaTime / math.max(1e-4f, cfg.Damping)); // tau=Damping
            float3 newPosition = snapP ? desiredPos : math.lerp(desiredPos, currPos, k);

            float3 lookAt = targetPos + new float3(0, cfg.LookUpBias, 0);
            float3 fwd    = math.normalizesafe(lookAt - newPosition, MathUtilities.GetForwardFromRotation(currRot));
            quaternion desiredLook = quaternion.LookRotationSafe(fwd, up);
            quaternion newRotation = snapR ? desiredLook : math.slerp(desiredLook, currRot, k);

            // 最后写回 LocalTransform
            LocalToWorldLookup[camera] = new LocalToWorld { Value = new float4x4(newRotation, newPosition) };
        }
        
    }
}
