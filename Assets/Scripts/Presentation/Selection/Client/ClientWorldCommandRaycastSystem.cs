using AnimarsCatcher.Gameplay;
using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 在表现阶段把最新屏幕点击解析为具有明确优先级的世界目标
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial struct ClientWorldCommandRaycastSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<WorldCommandClickRequest>();
            state.RequireForUpdate<WorldCommandRaycastResult>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var context = Object.FindFirstObjectByType<WorldCommandRaycastConfig>();
            if (context.WorldCamera == null)
                return;

            WorldCommandClickRequest requestRO = SystemAPI.GetSingleton<WorldCommandClickRequest>();
            RefRW<WorldCommandRaycastResult> result = SystemAPI.GetSingletonRW<WorldCommandRaycastResult>();

            if (requestRO.Version == result.ValueRO.Version)
                return;

            int version = requestRO.Version;
            float2 screenPos = requestRO.ScreenPosition;
            Vector3 screenPos3 = new Vector3(screenPos.x, screenPos.y, 0f);

            Ray ray = context.WorldCamera.ScreenPointToRay(screenPos3);

            WorldCommandTargetKind targetKind = WorldCommandTargetKind.None;
            Vector3 worldHitPoint = Vector3.zero;

            Camera cam = context.WorldCamera;

            Ray debugRay = cam.ScreenPointToRay(screenPos3);

            Debug.Log(
                $"[ClientWorldCommandRaycastSystem] cam={cam.name}, camPos={cam.transform.position}, " +
                $"camForward={cam.transform.forward}, screenPos={screenPos3}, " +
                $"rayOrigin={debugRay.origin}, rayDir={debugRay.direction}");

            // 玩家优先级最高，点击重叠时优先解释为跟随命令
            if (Physics.Raycast(ray, out RaycastHit hitPlayer, 1000f, context.PlayerMask))
            {
                targetKind = WorldCommandTargetKind.Player;
                worldHitPoint = hitPlayer.point;

                var hitEntity = hitPlayer.collider.gameObject.GetComponent<WorldCommandTargetProxy>()?.Entity ?? Entity.Null;
                if (hitEntity != Entity.Null)
                {
                    result.ValueRW.TargetEntity = hitEntity;
                }
            }

            // Ani 命中用于向服务器请求寻敌或拒绝友军目标
            else if (Physics.Raycast(ray, out RaycastHit hitAni, 1000f, context.AniMask))
            {
                targetKind = WorldCommandTargetKind.Ani;
                worldHitPoint = hitAni.point;

                var hitEntity = hitAni.collider.gameObject.GetComponent<WorldCommandTargetProxy>()?.Entity ?? Entity.Null;
                if (hitEntity != Entity.Null)
                {
                    result.ValueRW.TargetEntity = hitEntity;
                }
            }

            else if (Physics.Raycast(ray, out RaycastHit hitBase, 1000f, context.BaseMask))
            {
                targetKind = WorldCommandTargetKind.Base;
                worldHitPoint = hitBase.point;

                var hitEntity = hitBase.collider.gameObject.GetComponent<WorldCommandTargetProxy>()?.Entity ?? Entity.Null;
                if (hitEntity != Entity.Null)
                {
                    result.ValueRW.TargetEntity = hitEntity;
                }
            }

            // 资源优先于地面，确保模型覆盖区域仍能触发采集
            else if (Physics.Raycast(ray, out RaycastHit hitResource, 1000f, context.ResourceMask))
            {
                targetKind = WorldCommandTargetKind.Resource;
                worldHitPoint = hitResource.point;

                var hitEntity = hitResource.collider.gameObject.GetComponent<WorldCommandTargetProxy>()?.Entity ?? Entity.Null;
                if (hitEntity != Entity.Null)
                {
                    result.ValueRW.TargetEntity = hitEntity;
                }
            }

            // 只有没有命中交互 Entity 时才把点击作为地面移动处理
            else if (Physics.Raycast(ray, out RaycastHit hitGround, 1000f, context.GroundMask))
            {
                targetKind = WorldCommandTargetKind.Ground;
                worldHitPoint = hitGround.point;

                result.ValueRW.TargetEntity = Entity.Null;

                Debug.Log($"[ClientWorldCommandRaycastSystem] Ground hit at {worldHitPoint}, name ={hitGround.collider.gameObject.name}");
            }

            UnityEngine.Debug.Log($"[ClientWorldCommandRaycastSystem] Raycast result: Version={version}, TargetKind={targetKind}, WorldHitPoint={worldHitPoint}");

            // 即使没有命中也写回版本，避免同一次点击被每帧重复解析
            result.ValueRW.Version = version;
            result.ValueRW.TargetKind = targetKind;
            result.ValueRW.TargetWorldPosition = worldHitPoint;
        }
    }
}
