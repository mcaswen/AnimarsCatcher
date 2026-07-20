using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 配置服务端资源刷新区域 阻挡检测和波次上限
    /// </summary>
    [DisallowMultipleComponent]
    public class ResourceSpawnAreaAuthoring : MonoBehaviour
    {
        [Header("区域来源")]
        [FormerlySerializedAs("AreaBox")]
        [SerializeField] private BoxCollider _areaBox;

        [Tooltip("未配置 BoxCollider 时使用的 XZ 尺寸")]
        [FormerlySerializedAs("AreaSizeXZ")]
        [SerializeField] private Vector2 _areaSizeXZ = new Vector2(10f, 10f);

        [Tooltip("相对区域中心的生成高度偏移，单位米")]
        [FormerlySerializedAs("SpawnHeightOffset")]
        [SerializeField] private float _spawnHeightOffset;

        [Header("阻挡检测设置")]
        [FormerlySerializedAs("BlockerMask")]
        [SerializeField] private LayerMask _blockerMask;

        [FormerlySerializedAs("SpawnCheckRadius")]
        [SerializeField] private float _spawnCheckRadius = 0.5f;

        [FormerlySerializedAs("MaxSpawnAttemptsPerResource")]
        [SerializeField] private int _maximumSpawnAttemptsPerResource = 8;

        [Header("Food 刷新配置")]
        [FormerlySerializedAs("FoodPrefabs")]
        [SerializeField] private GameObject[] _foodPrefabs;

        [FormerlySerializedAs("MaxFoodCount")]
        [SerializeField] private int _maximumFoodCount = 5;

        [FormerlySerializedAs("FoodPerWave")]
        [SerializeField] private int _foodPerWave = 2;

        [Header("Crystal 刷新配置")]
        [FormerlySerializedAs("CrystalPrefabs")]
        [SerializeField] private GameObject[] _crystalPrefabs;

        [FormerlySerializedAs("MaxCrystalCount")]
        [SerializeField] private int _maximumCrystalCount = 5;

        [FormerlySerializedAs("CrystalPerWave")]
        [SerializeField] private int _crystalPerWave = 2;

        [Header("刷新节奏")]
        [FormerlySerializedAs("RespawnIntervalSeconds")]
        [SerializeField] private float _respawnIntervalSeconds = 5f;

        private sealed class Baker : Baker<ResourceSpawnAreaAuthoring>
        {
            public override void Bake(ResourceSpawnAreaAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.None);

                // 优先使用 BoxCollider 世界包围盒作为刷新范围
                BoxCollider box = authoring._areaBox != null
                    ? authoring._areaBox
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
                    halfExtentsXZ = authoring._areaSizeXZ * 0.5f;
                }

                ResourceSpawnArea area = new ResourceSpawnArea
                {
                    Center = center,
                    HalfExtentsXZ = halfExtentsXZ,
                    SpawnHeightOffset = authoring._spawnHeightOffset,

                    MaxFoodCount = math.max(0, authoring._maximumFoodCount),
                    MaxCrystalCount = math.max(0, authoring._maximumCrystalCount),

                    FoodPerWave = math.max(0, authoring._foodPerWave),
                    CrystalPerWave = math.max(0, authoring._crystalPerWave),

                    RespawnInterval = math.max(0.1f, authoring._respawnIntervalSeconds),
                    RespawnTimer = authoring._respawnIntervalSeconds - 0.5f,

                    SpawnCheckRadius = math.max(0.01f, authoring._spawnCheckRadius),
                    BlockerLayerMask = authoring._blockerMask.value,
                    MaxSpawnAttemptsPerResource = math.max(1, authoring._maximumSpawnAttemptsPerResource),

                    RandomSeed = (uint)UnityEngine.Random.Range(1, int.MaxValue)
                };

                AddComponent(entity, area);

                DynamicBuffer<FoodResourceSpawnPrefabReference> foodBuffer =
                    AddBuffer<FoodResourceSpawnPrefabReference>(entity);

                if (authoring._foodPrefabs != null)
                {
                    foreach (GameObject go in authoring._foodPrefabs)
                    {
                        if (!go) continue;
                        Entity prefabEntity = GetEntity(go, TransformUsageFlags.Dynamic);
                        foodBuffer.Add(new FoodResourceSpawnPrefabReference { Prefab = prefabEntity });
                    }
                }

                DynamicBuffer<CrystalResourceSpawnPrefabReference> crystalBuffer =
                    AddBuffer<CrystalResourceSpawnPrefabReference>(entity);

                if (authoring._crystalPrefabs != null)
                {
                    foreach (GameObject go in authoring._crystalPrefabs)
                    {
                        if (!go) continue;
                        Entity prefabEntity = GetEntity(go, TransformUsageFlags.Dynamic);
                        crystalBuffer.Add(new CrystalResourceSpawnPrefabReference { Prefab = prefabEntity });
                    }
                }
            }
        }
    }
}
