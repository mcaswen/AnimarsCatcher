using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ServerAssignSelectedAniToResourceSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        // 只要场景中出现了 PickableResource + ResourcePickupRequest 才更新
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
            // 已经有搬运任务了就忽略这次请求
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
