using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using Unity.CharacterController;
using UnityEngine;

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(TransformSystemGroup))]
// [UpdateAfter(typeof(ThirdPersonPlayerVariableStepControlSystem))]
// [UpdateAfter(typeof(ThirdPersonCharacterVariableUpdateSystem))]

[BurstCompile]
public partial struct OrbitCameraLateUpdateSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PhysicsWorldSingleton>();
        state.RequireForUpdate(SystemAPI.QueryBuilder().WithAll<OrbitCamera, OrbitCameraControl>().Build());
    }

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

                // Distance smoothing
                orbitCamera.SmoothedTargetDistance = math.lerp(orbitCamera.SmoothedTargetDistance, orbitCamera.TargetDistance, MathUtilities.GetSharpnessInterpolant(orbitCamera.DistanceMovementSharpness, DeltaTime));

                // Obstruction handling
                // Obstruction detection is handled here, because we have to adjust the obstruction distance
                // to match the interpolated physics body transform (as opposed to the "simulation" transform). Otherwise, a
                // camera getting obstructed by a moving physics body would have visible jitter.
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

                        // Redo cast with the interpolated body transform to prevent FixedUpdate jitter in obstruction detection
                        if (orbitCamera.PreventFixedUpdateJitter)
                        {
                            RigidBody hitBody = PhysicsWorld.Bodies[collector.ClosestHit.RigidBodyIndex];
                            if (LocalToWorldLookup.TryGetComponent(hitBody.Entity, out LocalToWorld hitBodyLocalToWorld))
                            {
                                // Adjust the rigidbody transform for interpolation, so we can raycast it in that state
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

                    // Update current distance based on obstructed distance
                    if (orbitCamera.ObstructedDistance < newObstructedDistance)
                    {
                        // Move outer
                        orbitCamera.ObstructedDistance = math.lerp(orbitCamera.ObstructedDistance, newObstructedDistance, MathUtilities.GetSharpnessInterpolant(orbitCamera.ObstructionOuterSmoothingSharpness, DeltaTime));
                    }
                    else if (orbitCamera.ObstructedDistance > newObstructedDistance)
                    {
                        // Move inner
                        orbitCamera.ObstructedDistance = math.lerp(orbitCamera.ObstructedDistance, newObstructedDistance, MathUtilities.GetSharpnessInterpolant(orbitCamera.ObstructionInnerSmoothingSharpness, DeltaTime));
                    }
                }
                else
                {
                    orbitCamera.ObstructedDistance = orbitCamera.SmoothedTargetDistance;
                }

                // Place camera at the final distance (includes smoothing and obstructions)
                float3 cameraPosition = OrbitCameraUtilities.CalculateCameraPosition(targetPosition, cameraRotation, orbitCamera.ObstructedDistance);

                // Write to LtW
                LocalToWorldLookup[entity] = new LocalToWorld { Value = new float4x4(cameraRotation, cameraPosition) };
            }
        }
    }
}
