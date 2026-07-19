using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在服务端为资源拾取请求分配目标玩家已选中的 Picker Ani
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ServerAssignSelectedAniToResourceSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            // 请求组件作为一次性命令存在时才运行
            state.RequireForUpdate(
                SystemAPI.QueryBuilder()
                    .WithAll<PickableResource, ResourcePickupRequest>()
                    .Build());
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            EntityCommandBuffer entityCommandBuffer =
                new EntityCommandBuffer(Allocator.Temp);

            foreach (var (pickable, pickupRequest, resourceEntity) in
                     SystemAPI.Query<RefRO<PickableResource>, RefRO<ResourcePickupRequest>>()
                         .WithEntityAccess())
            {
                // 同一资源只允许一个活动搬运任务
                if (SystemAPI.HasComponent<ResourceCarryAssignment>(resourceEntity))
                {
                    entityCommandBuffer.RemoveComponent<ResourcePickupRequest>(resourceEntity);
                    continue;
                }

                int maxCarrierFromConfig = pickable.ValueRO.MaximumCarrierAniCount;
                int maxCarrierFromRequest = pickupRequest.ValueRO.MaximumCarrierAniCountOverride;

                int maxCarrier = maxCarrierFromRequest > 0
                    ? maxCarrierFromRequest
                    : maxCarrierFromConfig;

                maxCarrier = math.max(1, maxCarrier);

                int assignedCount = 0;

                var owner = SystemAPI.GetComponent<GhostOwner>(pickupRequest.ValueRO.PlayerRobotEntity);
                int ownerNetworkId = owner.NetworkId;

                // 从当前选中的 Picker Ani 里挑
                foreach (var (pickerTag, selectedTag, ghostOwner, aniEntity) in
                         SystemAPI.Query<RefRO<PickerAniTag>, RefRO<AniSelectedTag>, RefRO<GhostOwner>>()
                             .WithEntityAccess())
                {
                    if (ghostOwner.ValueRO.NetworkId != ownerNetworkId)
                        continue;

                    if (!SystemAPI.IsComponentEnabled<AniSelectedTag>(aniEntity))
                        continue;

                    if (SystemAPI.IsComponentEnabled<AniCommandLockedTag>(aniEntity))
                        continue;

                    if (SystemAPI.HasComponent<AniCarryResourceOrder>(aniEntity))
                        continue;

                    entityCommandBuffer.AddComponent(aniEntity, new AniCarryResourceOrder
                    {
                        ResourceEntity = resourceEntity,
                        SlotIndex      = assignedCount
                    });

                    // 搬运中就先把选中状态关掉，避免玩家误操作
                    entityCommandBuffer.SetComponentEnabled<AniSelectedTag>(aniEntity, false);

                    assignedCount++;

                    if (assignedCount >= maxCarrier)
                        break;
                }

                if (assignedCount > 0)
                {
                    entityCommandBuffer.AddComponent(resourceEntity, new ResourceCarryAssignment
                    {
                        PlayerRobotEntity        = pickupRequest.ValueRO.PlayerRobotEntity,
                        AssignedCarrierAniCount  = assignedCount,
                        ReadyCarrierAniCount     = 0,
                        IsCarryStarted           = 0
                    });
                }

                UnityEngine.Debug.Log(
                    $"[ServerAssignSelectedAniToResourceSystem] Assigned {assignedCount}/{maxCarrier} Picker Ani to carry Resource Entity {resourceEntity.Index} for PlayerRobot Entity {pickupRequest.ValueRO.PlayerRobotEntity.Index}.");

                // 处理完请求就删掉
                entityCommandBuffer.RemoveComponent<ResourcePickupRequest>(resourceEntity);
            }

            entityCommandBuffer.Playback(state.EntityManager);
        }
    }
}
