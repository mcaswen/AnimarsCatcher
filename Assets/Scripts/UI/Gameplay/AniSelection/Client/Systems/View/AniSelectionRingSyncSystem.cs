using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.NetCode;


[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(PresentationSystemGroup))]
public partial struct SelectionRingSyncSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<SelectionRingPrefabConfig>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var config = SystemAPI.GetSingleton<SelectionRingPrefabConfig>();
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        // 生成选中Ani的光圈
        foreach (var (attributes, owner, aniEntity) in SystemAPI
                     .Query<RefRO<AniAttributes>, RefRO<GhostOwner>>()
                     .WithAll<AniSelectedTag>()
                     .WithNone<SelectionRingRef>()
                     .WithEntityAccess())
        {

            if (owner.ValueRO.NetworkId != SystemAPI.GetSingleton<NetworkId>().Value)
                continue;

            var ring = entityCommandBuffer.Instantiate(config.Prefab);

            // 作为 ani 的子物体，跟随移动
            entityCommandBuffer.AddComponent(ring, new Parent { Value = aniEntity });
            entityCommandBuffer.AddComponent(ring, new LocalTransform
            {
                Position = new float3(0f, config.YOffset, 0f),
                Rotation = quaternion.identity,
                Scale    = 1f
            });

            // 记录引用，避免重复生成
            entityCommandBuffer.AddComponent(aniEntity, new SelectionRingRef { RingEntity = ring });

            // 为了父物体死亡时一起清理：把子物体加入 LinkedEntityGroup
            if (!state.EntityManager.HasBuffer<LinkedEntityGroup>(aniEntity))
                entityCommandBuffer.AddBuffer<LinkedEntityGroup>(aniEntity);
            
            entityCommandBuffer.AppendToBuffer(aniEntity, new LinkedEntityGroup { Value = ring });
        }

        // 销毁未选中Ani的光圈
        foreach (var (ringRef, aniEntity) in SystemAPI
                     .Query<RefRO<SelectionRingRef>>()
                     .WithNone<AniSelectedTag>()
                     .WithEntityAccess())
        {
            var ring = ringRef.ValueRO.RingEntity;
            if (state.EntityManager.Exists(ring))
                entityCommandBuffer.DestroyEntity(ring);

            entityCommandBuffer.RemoveComponent<SelectionRingRef>(aniEntity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
