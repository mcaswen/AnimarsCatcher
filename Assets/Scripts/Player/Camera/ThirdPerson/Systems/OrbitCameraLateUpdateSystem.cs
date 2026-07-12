using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using Unity.CharacterController;
using UnityEngine;

/// <summary>
/// 在物理和 Transform 更新后修正环绕相机的平滑距离与遮挡
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(TransformSystemGroup))]
[UpdateAfter(typeof(ThirdPersonCharacterPhysicsUpdateSystem))]
[UpdateAfter(typeof(ThirdPersonCharacterVariableUpdateSystem))]

[BurstCompile]
public partial struct OrbitCameraLateUpdateSystem : ISystem
{
    /// <summary>
    /// 声明遮挡检测所需的物理世界和相机组件
    /// </summary>
    /// <param name="state">系统状态</param>
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PhysicsWorldSingleton>();
        state.RequireForUpdate(SystemAPI.QueryBuilder().WithAll<OrbitCamera, OrbitCameraControl>().Build());
    }

    /// <summary>
    /// 调度相机遮挡检测和最终姿态写回任务
    /// </summary>
    /// <param name="state">系统状态</param>
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        OrbitCameraLateUpdateJob job = new OrbitCameraLateUpdateJob
        {
            DeltaTime = SystemAPI.Time.DeltaTime,
            PhysicsWorld = SystemAPI.GetSingleton<PhysicsWorldSingleton>().PhysicsWorld,
            LocalToWorldLookup = SystemAPI.GetComponentLookup<LocalToWorld>(false),
            CameraTargetLookup = SystemAPI.GetComponentLookup<CameraTarget>(true),
            KinematicCharacterBodyLookup = SystemAPI.GetComponentLookup<KinematicCharacterBody>(true),
        };
        job.Schedule();
    }

    /// <summary>
    /// 使用插值后的物理姿态计算客户端最终相机位置
    /// </summary>
    [BurstCompile]
    [WithAll(typeof(Simulate))]
    public partial struct OrbitCameraLateUpdateJob : IJobEntity
    {
        public float DeltaTime;
        [ReadOnly]
        public PhysicsWorld PhysicsWorld;

        public ComponentLookup<LocalToWorld> LocalToWorldLookup;
        [ReadOnly]
        public ComponentLookup<CameraTarget> CameraTargetLookup;
        [ReadOnly]
        public ComponentLookup<KinematicCharacterBody> KinematicCharacterBodyLookup;

        void Execute(
            Entity entity,
            ref OrbitCamera orbitCamera,
            in OrbitCameraControl cameraControl,
            in DynamicBuffer<OrbitCameraIgnoredEntityBufferElement> ignoredEntitiesBuffer)
        {
            if (OrbitCameraUtilities.TryGetCameraTargetInterpolatedWorldTransform(
                    cameraControl.FollowedCharacterEntity,
                    ref LocalToWorldLookup,
                    ref CameraTargetLookup,
                    out LocalToWorld targetWorldTransform))
            {
                quaternion cameraRotation = OrbitCameraUtilities.CalculateCameraRotation(targetWorldTransform.Up, orbitCamera.PlanarForward, orbitCamera.PitchAngle);

                float3 cameraForward = math.mul(cameraRotation, math.forward());
                float3 targetPosition = targetWorldTransform.Position;

                // 平滑玩家输入产生的目标距离变化
                orbitCamera.SmoothedTargetDistance = math.lerp(orbitCamera.SmoothedTargetDistance, orbitCamera.TargetDistance, MathUtilities.GetSharpnessInterpolant(orbitCamera.DistanceMovementSharpness, DeltaTime));

                // 遮挡检测必须匹配插值后的刚体姿态，否则移动刚体会让相机在固定帧之间抖动
                if (orbitCamera.ObstructionRadius > 0f)
                {
                    float obstructionCheckDistance = orbitCamera.SmoothedTargetDistance;

                    CameraObstructionHitsCollector collector = new CameraObstructionHitsCollector(cameraControl.FollowedCharacterEntity, ignoredEntitiesBuffer, cameraForward);
                    PhysicsWorld.SphereCastCustom(
                        targetPosition,
                        orbitCamera.ObstructionRadius,
                        -cameraForward,
                        obstructionCheckDistance,
                        ref collector,
                        CollisionFilter.Default,
                        QueryInteraction.IgnoreTriggers);

                    float newObstructedDistance = obstructionCheckDistance;
                    if (collector.NumHits > 0)
                    {
                        newObstructedDistance = obstructionCheckDistance * collector.ClosestHit.Fraction;

                        // 使用命中刚体的插值姿态重新检测，消除 FixedUpdate 采样差导致的抖动
                        if (orbitCamera.PreventFixedUpdateJitter)
                        {
                            RigidBody hitBody = PhysicsWorld.Bodies[collector.ClosestHit.RigidBodyIndex];
                            if (LocalToWorldLookup.TryGetComponent(hitBody.Entity, out LocalToWorld hitBodyLocalToWorld))
                            {
                                // 临时替换刚体姿态，使二次球形投射基于插值位置
                                hitBody.WorldFromBody = new RigidTransform(quaternion.LookRotationSafe(hitBodyLocalToWorld.Forward, hitBodyLocalToWorld.Up), hitBodyLocalToWorld.Position);

                                collector = new CameraObstructionHitsCollector(cameraControl.FollowedCharacterEntity, ignoredEntitiesBuffer, cameraForward);
                                hitBody.SphereCastCustom(
                                    targetPosition,
                                    orbitCamera.ObstructionRadius,
                                    -cameraForward,
                                    obstructionCheckDistance,
                                    ref collector,
                                    CollisionFilter.Default,
                                    QueryInteraction.IgnoreTriggers);

                                if (collector.NumHits > 0)
                                {
                                    newObstructedDistance = obstructionCheckDistance * collector.ClosestHit.Fraction;
                                }
                            }
                        }
                    }

                    // 靠近遮挡物时快速收缩，远离遮挡物时平滑恢复距离
                    if (orbitCamera.ObstructedDistance < newObstructedDistance)
                    {
                        // 向外恢复使用较缓的平滑参数
                        orbitCamera.ObstructedDistance = math.lerp(orbitCamera.ObstructedDistance, newObstructedDistance, MathUtilities.GetSharpnessInterpolant(orbitCamera.ObstructionOuterSmoothingSharpness, DeltaTime));
                    }
                    else if (orbitCamera.ObstructedDistance > newObstructedDistance)
                    {
                        // 向内收缩使用较快的平滑参数
                        orbitCamera.ObstructedDistance = math.lerp(orbitCamera.ObstructedDistance, newObstructedDistance, MathUtilities.GetSharpnessInterpolant(orbitCamera.ObstructionInnerSmoothingSharpness, DeltaTime));
                    }
                }
                else
                {
                    orbitCamera.ObstructedDistance = orbitCamera.SmoothedTargetDistance;
                }

                // 使用平滑且经过遮挡修正的距离计算最终位置
                float3 cameraPosition = OrbitCameraUtilities.CalculateCameraPosition(targetPosition, cameraRotation, orbitCamera.ObstructedDistance);

                // 写入 LocalToWorld，供主相机表现系统读取
                LocalToWorldLookup[entity] = new LocalToWorld { Value = new float4x4(cameraRotation, cameraPosition) };
            }
        }
    }
}
