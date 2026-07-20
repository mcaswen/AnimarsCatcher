using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 配置可破坏水晶及其掉落资源预制体
    /// </summary>
    [DisallowMultipleComponent]
    public class FragileCrystalAuthoring : MonoBehaviour
    {
        [Header("掉落配置")]
        [FormerlySerializedAs("DropKind")]
        [SerializeField] private ResourceItemKind _dropKind = ResourceItemKind.Crystal;

        [FormerlySerializedAs("DropPieceCount")]
        [SerializeField] private int _dropPieceCount = 3;

        [FormerlySerializedAs("DropSpawnRadius")]
        [SerializeField] private float _dropSpawnRadius = 1.5f;

        [Header("掉落小矿 Prefab")]
        [FormerlySerializedAs("PickablePrefab")]
        [SerializeField] private GameObject _pickablePrefab;

        private sealed class Baker : Baker<FragileCrystalAuthoring>
        {
            public override void Bake(FragileCrystalAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);

                Entity pickablePrefabEntity = Entity.Null;
                if (authoring._pickablePrefab != null)
                {
                    // 将掉落 GameObject 转换为可实例化的 Ghost 预制体实体
                    pickablePrefabEntity =
                        GetEntity(authoring._pickablePrefab, TransformUsageFlags.Dynamic);
                }

                AddComponent(entity, new FragileCrystal
                {
                    DropKind         = authoring._dropKind,
                    DropPieceCount   = authoring._dropPieceCount,
                    DropSpawnRadius  = authoring._dropSpawnRadius,
                    PickablePrefab   = pickablePrefabEntity
                });

                // 同时加入攻击和资源标签供战斗查询过滤
                AddComponent<AttackableResourceTag>(entity);
                AddComponent<ResourceItemTag>(entity);
                AddComponent<RangedAttackableTag>(entity);
            }
        }
    }
}
