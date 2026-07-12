using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 配置可破坏水晶及其掉落资源预制体
/// </summary>
[DisallowMultipleComponent]
public class FragileCrystalAuthoring : MonoBehaviour
{
    [Header("掉落配置")]
    public ResourceItemKind DropKind = ResourceItemKind.Crystal;
    public int DropPieceCount = 3;

    public float DropSpawnRadius = 1.5f;

    [Header("掉落小矿 Prefab")]
    public GameObject PickablePrefab;

    /// <summary>
    /// 将掉落配置和资源标签烘焙到实体
    /// </summary>
    class Baker : Baker<FragileCrystalAuthoring>
    {
        public override void Bake(FragileCrystalAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.Dynamic);

            Entity pickablePrefabEntity = Entity.Null;
            if (authoring.PickablePrefab != null)
            {
                // 将掉落 GameObject 转换为可实例化的 Ghost 预制体实体
                pickablePrefabEntity =
                    GetEntity(authoring.PickablePrefab, TransformUsageFlags.Dynamic);
            }

            AddComponent(entity, new FragileCrystal
            {
                DropKind         = authoring.DropKind,
                DropPieceCount   = authoring.DropPieceCount,
                DropSpawnRadius  = authoring.DropSpawnRadius,
                PickablePrefab   = pickablePrefabEntity
            });

            // 同时加入攻击和资源标签供战斗查询过滤
            AddComponent<AttackableResourceTag>(entity);
            AddComponent<ResourceItemTag>(entity);
            AddComponent<RangedAttackableTag>(entity);
        }
    }
}
