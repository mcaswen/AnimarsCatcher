using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(PresentationSystemGroup))]
public partial struct MovementClickRaycastSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MovementClickRequest>();
        state.RequireForUpdate<MovementClickResult>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var context = Object.FindFirstObjectByType<MovementRaycastBootstrp>();
        if (context.WorldCamera == null)
            return;

        MovementClickRequest requestRO = SystemAPI.GetSingleton<MovementClickRequest>();
        RefRW<MovementClickResult> result = SystemAPI.GetSingletonRW<MovementClickResult>();

        if (requestRO.Version == result.ValueRO.Version)
            return;

        int version = requestRO.Version;
        float2 screenPos = requestRO.ScreenPosition;
        Vector3 screenPos3 = new Vector3(screenPos.x, screenPos.y, 0f);

        Ray ray = context.WorldCamera.ScreenPointToRay(screenPos3);

        MovementTargetKind targetKind = MovementTargetKind.None;
        Vector3 worldHitPoint = Vector3.zero;

        // 先检测 Player
        if (Physics.Raycast(ray, out RaycastHit hitPlayer, 1000f, context.PlayerMask))
        {
            targetKind = MovementTargetKind.Player;
            worldHitPoint = hitPlayer.point;

            var hitEntity = hitPlayer.collider.gameObject.GetComponent<MovementSelectableProxy>()?.Entity ?? Entity.Null;
            if (hitEntity != Entity.Null)
            {
                result.ValueRW.TargetEntity = hitEntity;
            }
        }

        // 再检测 Ani
        else if (Physics.Raycast(ray, out RaycastHit hitAni, 1000f, context.AniMask))
        {
            targetKind = MovementTargetKind.Ani;
            worldHitPoint = hitAni.point;

            var hitEntity = hitAni.collider.gameObject.GetComponent<MovementSelectableProxy>()?.Entity ?? Entity.Null;
            if (hitEntity != Entity.Null)
            {
                result.ValueRW.TargetEntity = hitEntity;
            }
        }

        // 再检测 Resource
        else if (Physics.Raycast(ray, out RaycastHit hitResource, 1000f, context.ResourceMask))
        {
            targetKind = MovementTargetKind.Resource;
            worldHitPoint = hitResource.point;

            var hitEntity = hitResource.collider.gameObject.GetComponent<MovementSelectableProxy>()?.Entity ?? Entity.Null;
            if (hitEntity != Entity.Null)
            {
                result.ValueRW.TargetEntity = hitEntity;
            }
        }
        
        // 最后是 Ground
        else if (Physics.Raycast(ray, out RaycastHit hitGround, 1000f, context.GroundMask))
        {
            targetKind = MovementTargetKind.Ground;
            worldHitPoint = hitGround.point;

            result.ValueRW.TargetEntity = Entity.Null;
        }

        result.ValueRW.Version = version;
        result.ValueRW.TargetKind = targetKind;
        result.ValueRW.TargetWorldPosition = worldHitPoint;
    }
}
