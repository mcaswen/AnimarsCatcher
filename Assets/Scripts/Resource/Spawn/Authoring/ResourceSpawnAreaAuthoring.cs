using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 配置服务端资源刷新区域 阻挡检测和波次上限
    /// </summary>
    [DisallowMultipleComponent]
    public class ResourceSpawnAreaAuthoring : MonoBehaviour
    {
        [Header("区域来源")]
        public BoxCollider AreaBox;

        [Tooltip("未配置 BoxCollider 时使用的 XZ 尺寸")]
        public Vector2 AreaSizeXZ = new Vector2(10f, 10f);

        [Tooltip("相对区域中心的生成高度偏移，单位米")]
        public float SpawnHeightOffset = 0f;

        [Header("阻挡检测设置")]
        public LayerMask BlockerMask;           // 生成点不可重叠的地形 资源和建筑层
        public float SpawnCheckRadius = 0.5f;   // 阻挡检测球半径
        public int MaxSpawnAttemptsPerResource = 8;

        [Header("Food 刷新配置")]
        public GameObject[] FoodPrefabs;
        public int MaxFoodCount = 5;
        public int FoodPerWave  = 2;

        [Header("Crystal 刷新配置")]
        public GameObject[] CrystalPrefabs;

        public int MaxCrystalCount = 5;
        public int CrystalPerWave  = 2;

        [Header("刷新节奏")]
        public float RespawnIntervalSeconds = 5f;

        class Baker : Baker<ResourceSpawnAreaAuthoring>
        {
            public override void Bake(ResourceSpawnAreaAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.None);

                // 优先使用 BoxCollider 世界包围盒作为刷新范围
                BoxCollider box = authoring.AreaBox != null
                    ? authoring.AreaBox
                    : authoring.GetComponent<BoxCollider>();

                float3 center;
                float2 halfExtentsXZ;

                if (box != null)
                {
                    Bounds bounds = box.bounds;

                    center = (float3)bounds.center;
                    halfExtentsXZ = new float2(bounds.extents.x, bounds.extents.z);
                }
                else
                {
                    center = authoring.transform.position;
                    halfExtentsXZ = authoring.AreaSizeXZ * 0.5f;
                }

                ResourceSpawnArea area = new ResourceSpawnArea
                {
                    Center = center,
                    HalfExtentsXZ = halfExtentsXZ,
                    SpawnHeightOffset = authoring.SpawnHeightOffset,

                    MaxFoodCount = math.max(0, authoring.MaxFoodCount),
                    MaxCrystalCount = math.max(0, authoring.MaxCrystalCount),

                    FoodPerWave = math.max(0, authoring.FoodPerWave),
                    CrystalPerWave = math.max(0, authoring.CrystalPerWave),

                    RespawnInterval = math.max(0.1f, authoring.RespawnIntervalSeconds),
                    RespawnTimer = authoring.RespawnIntervalSeconds - 0.5f,

                    SpawnCheckRadius = math.max(0.01f, authoring.SpawnCheckRadius),
                    BlockerLayerMask = authoring.BlockerMask.value,
                    MaxSpawnAttemptsPerResource = math.max(1, authoring.MaxSpawnAttemptsPerResource),

                    RandomSeed = (uint)UnityEngine.Random.Range(1, int.MaxValue)
                };

                AddComponent(entity, area);

                DynamicBuffer<ResourceSpawnFoodPrefab> foodBuffer =
                    AddBuffer<ResourceSpawnFoodPrefab>(entity);

                if (authoring.FoodPrefabs != null)
                {
                    foreach (GameObject go in authoring.FoodPrefabs)
                    {
                        if (!go) continue;
                        Entity prefabEntity = GetEntity(go, TransformUsageFlags.Dynamic);
                        foodBuffer.Add(new ResourceSpawnFoodPrefab { Prefab = prefabEntity });
                    }
                }

                DynamicBuffer<ResourceSpawnCrystalPrefab> crystalBuffer =
                    AddBuffer<ResourceSpawnCrystalPrefab>(entity);

                if (authoring.CrystalPrefabs != null)
                {
                    foreach (GameObject go in authoring.CrystalPrefabs)
                    {
                        if (!go) continue;
                        Entity prefabEntity = GetEntity(go, TransformUsageFlags.Dynamic);
                        crystalBuffer.Add(new ResourceSpawnCrystalPrefab { Prefab = prefabEntity });
                    }
                }
            }
        }
    }
}
