using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

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
    // 要刷出的基地预制体（Ghost 或普通 Entity 都可以）
    public Entity BasePrefab;

    // 元信息：在 Authoring 里硬编码，方便以后逻辑用
    public CampType CampKind;
    public BaseSizeKind SizeKind;

    // 是否已经刷过（0 = 未刷，1 = 已刷）
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
    public GameObject BasePrefab;

    [Header("阵营：Alpha / Beta")]
    public CampType CampKind = CampType.Alpha;

    [Header("基地大小：小基地 / 大基地")]
    public BaseSizeKind SizeKind = BaseSizeKind.Small;

    [Header("血量")]
    public int Health = 1000;

    class Baker : Baker<BaseSpawnPointAuthoring>
    {
        public override void Bake(BaseSpawnPointAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.Dynamic);

            if (!authoring.BasePrefab)
            {
                Debug.LogWarning($"[BaseSpawnPointAuthoring] {authoring.name} 没有配置 BasePrefab");
                return;
            }

            Entity prefabEntity = GetEntity(authoring.BasePrefab, TransformUsageFlags.Dynamic);

            AddComponent(entity, new BaseSpawnPoint
            {
                BasePrefab = prefabEntity,
                CampKind   = authoring.CampKind,
                SizeKind   = authoring.SizeKind,
                HasSpawned = 0,
                Health     = authoring.Health
            });

            // 刷新点本身的位置，用它当基地出生点
            AddComponent(entity, new LocalTransform
            {
                Position = authoring.transform.position,
                Rotation = authoring.transform.rotation,
                Scale    = 1f
            });
        }
    }
}
