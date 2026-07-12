using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 配置实体对应的血条 GameObject 预制体和世界偏移
/// </summary>
[DisallowMultipleComponent]
public class HealthBarViewAuthoring : MonoBehaviour
{
    [Tooltip("血条 UI 预制体")]
    public GameObject healthBarPrefab;

    [Tooltip("世界空间偏移")]
    public Vector3 worldOffset = new Vector3(0f, 2f, 0f);

    /// <summary>
    /// 将托管血条预制体引用烘焙到目标实体
    /// </summary>
    class Baker : Baker<HealthBarViewAuthoring>
    {
        public override void Bake(HealthBarViewAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.Dynamic);

            AddComponentObject(entity, new HealthBarViewPrefab
            {
                healthBarPrefab = authoring.healthBarPrefab,
                worldOffset = authoring.worldOffset
            });
        }
    }
}

/// <summary>
/// ECS 实体持有的托管血条预制体配置
/// </summary>
public class HealthBarViewPrefab : IComponentData
{
    public GameObject healthBarPrefab;
    public Vector3 worldOffset;
}

/// <summary>
/// 标识目标实体已经创建客户端血条视图
/// </summary>
public struct HealthBarViewSpawnedTag : IComponentData {}
