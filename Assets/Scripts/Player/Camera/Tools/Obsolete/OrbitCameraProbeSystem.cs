using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using Unity.Mathematics;

[UpdateInGroup(typeof(SimulationSystemGroup))]
// [UpdateAfter(typeof(ThirdPersonPlayerVariableStepControlSystem))]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.LocalSimulation)]
public partial class OrbitCameraProbeSystem : SystemBase
{
    protected override void OnCreate()
    {
        Enabled = false; //调试完毕，暂时禁用
    }

    protected override void OnUpdate()
    {
        if (!SystemAPI.TryGetSingletonEntity<MainEntityCamera>(out var mainCameraEntity)) { Debug.Log("[Probe] no MainEntityCamera"); return; }

        var entityManager = EntityManager;
        var localToWorld = entityManager.GetComponentData<LocalToWorld>(mainCameraEntity);
        var orbitCamera = entityManager.GetComponentData<OrbitCamera>(mainCameraEntity);
        var orbitCameraControl = entityManager.GetComponentData<OrbitCameraControl>(mainCameraEntity);

        if (entityManager.HasComponent<CameraTarget>(orbitCameraControl.FollowedCharacterEntity))
        {
            var cameraTarget = entityManager.GetComponentData<CameraTarget>(orbitCameraControl.FollowedCharacterEntity).TargetEntity;
            Debug.Log($"[Probe] Using CameraTarget.TargetEntity = {cameraTarget}");
        }
        else
        {
            Debug.Log($"[Probe] Using FollowedCharacterEntity = {orbitCameraControl.FollowedCharacterEntity}");
        }

        var followedCharacterEntity = orbitCameraControl.FollowedCharacterEntity;

        // A. 跟随主体（角色本体）
        bool followedCharacterExists = entityManager.Exists(followedCharacterEntity);
        bool followedCharacterHasLocalTransform = entityManager.HasComponent<LocalTransform>(followedCharacterEntity);
        bool followedCharacterHasLocalToWorld = entityManager.HasComponent<LocalToWorld>(followedCharacterEntity);
        float3 followedCharacterPosition = followedCharacterHasLocalToWorld
            ? entityManager.GetComponentData<LocalToWorld>(followedCharacterEntity).Position
            : (followedCharacterHasLocalTransform
                ? entityManager.GetComponentData<LocalTransform>(followedCharacterEntity).Position
                : default);
        Debug.Log($"[Target/FOLLOW] exists={followedCharacterExists} hasLocalTransform={followedCharacterHasLocalTransform} hasLocalToWorld={followedCharacterHasLocalToWorld} position={followedCharacterPosition}");

        // B. CameraTarget.TargetEntity（真正的瞄准挂点）
        Entity target = default;
        if (entityManager.HasComponent<CameraTarget>(followedCharacterEntity))
        {
            target = entityManager.GetComponentData<CameraTarget>(followedCharacterEntity).TargetEntity;
            bool targetExists = entityManager.Exists(target);
            bool targetHasLocalTransform = entityManager.HasComponent<LocalTransform>(target);
            bool targetHasLocalToWorld = entityManager.HasComponent<LocalToWorld>(target);
            float3 targetPosition = targetHasLocalToWorld
                ? entityManager.GetComponentData<LocalToWorld>(target).Position
                : (targetHasLocalTransform
                    ? entityManager.GetComponentData<LocalTransform>(target).Position
                    : default);
            Debug.Log($"[Target/TARGET] entity={target.Index}:{target.Version} exists={targetExists} hasLocalTransform={targetHasLocalTransform} hasLocalToWorld={targetHasLocalToWorld} position={targetPosition}");
        }

    }
}
