using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using Unity.Mathematics;

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ThirdPersonPlayerVariableStepControlSystem))]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.LocalSimulation)]
public partial class OrbitCamProbeSystem : SystemBase
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

        var em = EntityManager;
        var cam = SystemAPI.GetSingletonEntity<MainEntityCamera>();
        var ctl = em.GetComponentData<OrbitCameraControl>(cam);

        var useFollow = ctl.FollowedCharacterEntity;

        // A. 跟随主体（角色本体）
        bool fExists = em.Exists(useFollow);
        bool fHasLT = em.HasComponent<LocalTransform>(useFollow);
        bool fHasLTW = em.HasComponent<LocalToWorld>(useFollow);
        float3 fPos = fHasLTW ? em.GetComponentData<LocalToWorld>(useFollow).Position
                            : (fHasLT ? em.GetComponentData<LocalTransform>(useFollow).Position : default);
        Debug.Log($"[Target/FOLLOW] exists={fExists} hasLT={fHasLT} hasLTW={fHasLTW} pos={fPos}");

        // B. CameraTarget.TargetEntity（真正的瞄准挂点）
        Entity target = default;
        if (em.HasComponent<CameraTarget>(useFollow))
        {
            target = em.GetComponentData<CameraTarget>(useFollow).TargetEntity;
            bool targetExists = em.Exists(target);
            bool targetHasLT = em.HasComponent<LocalTransform>(target);
            bool targetHasLTW = em.HasComponent<LocalToWorld>(target);
            float3 targetPos = targetHasLTW ? em.GetComponentData<LocalToWorld>(target).Position
                                : (targetHasLT ? em.GetComponentData<LocalTransform>(target).Position : default);
            Debug.Log($"[Target/TARGET] e={target.Index}:{target.Version} exists={targetExists} hasLT={targetHasLT} hasLTW={targetHasLTW} pos={targetPos}");
        }

    }
}
