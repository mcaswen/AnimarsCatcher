using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Serialization;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 区分基地规模并为后续规则提供稳定枚举值
    /// </summary>
    public enum BaseSizeKind : byte
    {
        Small = 0,
        Big   = 1
    }

    /// <summary>
    /// 描述服务器需要实例化的基地及其出生参数
    /// </summary>
    public struct BaseSpawnPoint : IComponentData
    {
        // 由服务器实例化的基地预制体
        public Entity BasePrefab;

        public CampType CampKind;
        public BaseSizeKind SizeKind;

        // 0 表示尚未生成，1 表示已生成
        public byte HasSpawned;

        public int Health;
    }

    /// <summary>
    /// 在场景中配置基地预制体、阵营、规模和初始生命值
    /// </summary>
    [DisallowMultipleComponent]
    public class BaseSpawnPointAuthoring : MonoBehaviour
    {
        [Header("刷出的基地预制体（Prefab 里自己配好 Camp、血条、碰撞体）")]
        [FormerlySerializedAs("BasePrefab")]
        [SerializeField] private GameObject _basePrefab;

        [Header("阵营：Alpha / Beta")]
        [FormerlySerializedAs("CampKind")]
        [SerializeField] private CampType _camp = CampType.Alpha;

        [Header("基地大小：小基地 / 大基地")]
        [FormerlySerializedAs("SizeKind")]
        [SerializeField] private BaseSizeKind _size = BaseSizeKind.Small;

        [Header("血量")]
        [FormerlySerializedAs("Health")]
        [SerializeField] private int _health = 1000;

        private sealed class Baker : Baker<BaseSpawnPointAuthoring>
        {
            public override void Bake(BaseSpawnPointAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);

                if (!authoring._basePrefab)
                {
                    Debug.LogWarning($"[BaseSpawnPointAuthoring] {authoring.name} 没有配置 BasePrefab");
                    return;
                }

                Entity prefabEntity = GetEntity(authoring._basePrefab, TransformUsageFlags.Dynamic);

                AddComponent(entity, new BaseSpawnPoint
                {
                    BasePrefab = prefabEntity,
                    CampKind   = authoring._camp,
                    SizeKind   = authoring._size,
                    HasSpawned = 0,
                    Health     = authoring._health
                });

                // 烘焙刷新点变换，生成时无需再访问场景对象
                AddComponent(entity, new LocalTransform
                {
                    Position = authoring.transform.position,
                    Rotation = authoring.transform.rotation,
                    Scale    = 1f
                });
            }
        }
    }
}
