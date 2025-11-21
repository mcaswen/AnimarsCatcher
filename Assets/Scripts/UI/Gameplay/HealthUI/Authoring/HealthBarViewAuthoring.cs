using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

[DisallowMultipleComponent]
public class HealthBarViewAuthoring : MonoBehaviour
{
    [Tooltip("血条 UI 预制体")]
    public GameObject healthBarPrefab;

    [Tooltip("世界空间偏移")]
    public Vector3 worldOffset = new Vector3(0f, 2f, 0f);

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

// 存 GameObject 引用 + 偏移
public class HealthBarViewPrefab : IComponentData
{
    public GameObject healthBarPrefab;
    public Vector3 worldOffset;
}

// 标记：表示这个实体的血条已经生成
public struct HealthBarViewSpawnedTag : IComponentData {}
