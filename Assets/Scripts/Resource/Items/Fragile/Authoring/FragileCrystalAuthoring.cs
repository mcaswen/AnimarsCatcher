using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

[DisallowMultipleComponent]
public class FragileCrystalAuthoring : MonoBehaviour
{
    [Header("掉落配置")]
    public ResourceItemKind DropKind = ResourceItemKind.Crystal;
    public int DropPieceCount = 3;         // 掉几块小矿

    public float DropSpawnRadius = 1.5f;   // 掉落范围半径（世界空间）

    [Header("掉落小矿 Prefab")]
    public GameObject PickablePrefab;

    class Baker : Baker<FragileCrystalAuthoring>
    {
        public override void Bake(FragileCrystalAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.Dynamic);

            Entity pickablePrefabEntity = Entity.Null;
            if (authoring.PickablePrefab != null)
            {
                // 这里拿到的是对应的 Ghost prefab entity
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

            // 标记为“可被攻击的资源”，后面 Ani 攻击系统可以用
            AddComponent<AttackableResourceTag>(entity);
            AddComponent<ResourceItemTag>(entity);
            AddComponent<RangedAttackableTag>(entity);
        }
    }
}
