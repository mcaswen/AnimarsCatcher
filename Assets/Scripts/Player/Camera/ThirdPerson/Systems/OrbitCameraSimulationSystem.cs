namespace AnimarsCatcher.Player
{
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.Physics;
    using Unity.Transforms;
    using Unity.CharacterController;
    using UnityEngine;

    /// <summary>
    /// 在模拟阶段根据玩家输入计算环绕相机的目标姿态
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(TransformSystemGroup))]
    [BurstCompile]
    public partial struct OrbitCameraSimulationSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate(SystemAPI.QueryBuilder().WithAll<OrbitCamera, OrbitCameraControl>().Build());
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            OrbitCameraSimulationJob job = new OrbitCameraSimulationJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                LocalTransformLookup = SystemAPI.GetComponentLookup<LocalTransform>(false),
                ParentLookup = SystemAPI.GetComponentLookup<Parent>(true),
                PostTransformMatrixLookup = SystemAPI.GetComponentLookup<PostTransformMatrix>(true),
                CameraTargetLookup = SystemAPI.GetComponentLookup<CameraTarget>(true),
                KinematicCharacterBodyLookup = SystemAPI.GetComponentLookup<KinematicCharacterBody>(true),
            };
            job.Schedule();
        }

        /// <summary>
        /// 并行计算每个环绕相机的目标旋转、距离和未修正位置
        /// </summary>
        [BurstCompile]
        [WithAll(typeof(Simulate))]
        public partial struct OrbitCameraSimulationJob : IJobEntity
        {
            public float DeltaTime;

            public ComponentLookup<LocalTransform> LocalTransformLookup;
            [ReadOnly] public ComponentLookup<Parent> ParentLookup;
            [ReadOnly] public ComponentLookup<PostTransformMatrix> PostTransformMatrixLookup;
            [ReadOnly] public ComponentLookup<CameraTarget> CameraTargetLookup;
            [ReadOnly] public ComponentLookup<KinematicCharacterBody> KinematicCharacterBodyLookup;

            void Execute(Entity entity, ref OrbitCamera orbitCamera, in OrbitCameraControl cameraControl)
            {
                if (OrbitCameraUtilities.TryGetCameraTargetSimulationWorldTransform(
                        cameraControl.FollowedCharacterEntity,
                        ref LocalTransformLookup,
                        ref ParentLookup,
                        ref PostTransformMatrixLookup,
                        ref CameraTargetLookup,
                        out float4x4 targetWorldTransform))
                {
                    float3 targetUp = targetWorldTransform.Up();
                    float3 targetPosition = targetWorldTransform.Translation();

                    // 先让平面前方向适配目标的上方向以及父 Entity 旋转
                    {
                        quaternion tmpPlanarRotation = MathUtilities.CreateRotationWithUpPriority(targetUp, orbitCamera.PlanarForward);

                        // 角色站在旋转平台上时继承父 Entity 的平面旋转
                        if (orbitCamera.RotateWithCharacterParent &&
                            KinematicCharacterBodyLookup.TryGetComponent(cameraControl.FollowedCharacterEntity, out KinematicCharacterBody characterBody))
                        {
                            // 相机已单独适配角色上方向，此处只叠加绕上方向的父级旋转
                            quaternion planarRotationFromParent = characterBody.RotationFromParent;
                            KinematicCharacterUtilities.AddVariableRateRotationFromFixedRateRotation(ref tmpPlanarRotation, planarRotationFromParent, DeltaTime, characterBody.LastPhysicsUpdateDeltaTime);
                        }

                        orbitCamera.PlanarForward = MathUtilities.GetForwardFromRotation(tmpPlanarRotation);
                    }

                    // 应用本帧偏航输入
                    float yawAngleChange = cameraControl.LookDegreesDelta.x * orbitCamera.RotationSpeed;
                    quaternion yawRotation = quaternion.Euler(targetUp * math.radians(yawAngleChange));
                    orbitCamera.PlanarForward = math.rotate(yawRotation, orbitCamera.PlanarForward);

                    // 累计俯仰输入并限制垂直视角
                    orbitCamera.PitchAngle += -cameraControl.LookDegreesDelta.y * orbitCamera.RotationSpeed;
                    orbitCamera.PitchAngle = math.clamp(orbitCamera.PitchAngle, orbitCamera.MinVerticalAngle, orbitCamera.MaxVerticalAngle);

                    // 合成最终相机旋转
                    quaternion cameraRotation = OrbitCameraUtilities.CalculateCameraRotation(targetUp, orbitCamera.PlanarForward, orbitCamera.PitchAngle);

                    // 将缩放输入转换为目标距离
                    float desiredDistanceMovementFromInput = cameraControl.ZoomDelta * orbitCamera.DistanceMovementSpeed;
                    orbitCamera.TargetDistance = math.clamp(orbitCamera.TargetDistance + desiredDistanceMovementFromInput, orbitCamera.MinDistance, orbitCamera.MaxDistance);

                    // 此阶段只计算目标位置，平滑和遮挡修正在后续系统执行
                    float3 cameraPosition = OrbitCameraUtilities.CalculateCameraPosition(targetPosition, cameraRotation, orbitCamera.TargetDistance);

                    // 写回模拟姿态供后续 Transform 和遮挡系统使用
                    LocalTransformLookup[entity] = LocalTransform.FromPositionRotation(cameraPosition, cameraRotation);
                }
            }
        }
    }
}
