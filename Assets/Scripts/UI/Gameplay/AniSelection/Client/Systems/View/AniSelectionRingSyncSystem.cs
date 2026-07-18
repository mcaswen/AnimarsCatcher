using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.NetCode;


/// <summary>
/// 在客户端为本地玩家已选 Ani 创建并回收选中光圈
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(PresentationSystemGroup))]
public partial struct AniSelectionRingSyncSystem : ISystem
{
    /// <summary>
    /// 等待光圈预制体配置完成烘焙
    /// </summary>
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<SelectionRingPrefabConfig>();
    }

    /// <summary>
    /// 根据 AniSelectedTag 的启用状态同步光圈实体
    /// </summary>
    public void OnUpdate(ref SystemState state)
    {
        var config = SystemAPI.GetSingleton<SelectionRingPrefabConfig>();
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        // 只为本地玩家且尚无光圈的已选 Ani 创建实例
        foreach (var (attributes, owner, aniEntity) in SystemAPI
                     .Query<RefRO<AniAttributes>, RefRO<GhostOwner>>()
                     .WithAll<AniSelectedTag>()
                     .WithNone<SelectionRingReference>()
                     .WithEntityAccess())
        {

            if (owner.ValueRO.NetworkId != SystemAPI.GetSingleton<NetworkId>().Value)
                continue;

            var ring = entityCommandBuffer.Instantiate(config.Prefab);

            // 设为 Ani 子实体以自动跟随位置和生命周期
            entityCommandBuffer.AddComponent(ring, new Parent { Value = aniEntity });
            entityCommandBuffer.AddComponent(ring, new LocalTransform
            {
                Position = new float3(0f, config.YOffset, 0f),
                Rotation = quaternion.identity,
                Scale    = 1f
            });

            // 保存引用作为幂等标记
            entityCommandBuffer.AddComponent(aniEntity, new SelectionRingReference { RingEntity = ring });

            // 加入 LinkedEntityGroup 让父实体销毁时级联清理
            if (!state.EntityManager.HasBuffer<LinkedEntityGroup>(aniEntity))
                entityCommandBuffer.AddBuffer<LinkedEntityGroup>(aniEntity);
            
            entityCommandBuffer.AppendToBuffer(aniEntity, new LinkedEntityGroup { Value = ring });
        }

        // 未选中 Ani 的光圈立即回收并移除引用标记
        foreach (var (ringRef, aniEntity) in SystemAPI
                     .Query<RefRO<SelectionRingReference>>()
                     .WithNone<AniSelectedTag>()
                     .WithEntityAccess())
        {
            var ring = ringRef.ValueRO.RingEntity;
            if (state.EntityManager.Exists(ring))
                entityCommandBuffer.DestroyEntity(ring);

            entityCommandBuffer.RemoveComponent<SelectionRingReference>(aniEntity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
