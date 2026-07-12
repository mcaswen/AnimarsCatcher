using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 运行时角色盒体的本地中心和半尺寸
/// </summary>
public struct CharacterBoxInfo : IComponentData
{
    public float3 Center; // 相对实体原点的本地偏移
    public float3 HalfExtents; // 盒体在三个轴向上的半尺寸
}

/// <summary>
/// 从场景 BoxCollider 烘焙 ECS 盒体尺寸
/// </summary>
[DisallowMultipleComponent]
public class CharacterBoxAuthoring : MonoBehaviour
{
    public BoxCollider SourceCollider;

    /// <summary>
    /// 读取显式引用或同对象上的 BoxCollider
    /// </summary>
    class Baker : Baker<CharacterBoxAuthoring>
    {
        /// <summary>
        /// 将 Unity 盒体尺寸转换为 ECS 半尺寸数据
        /// </summary>
        public override void Bake(CharacterBoxAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            var box = authoring.SourceCollider != null
                ? authoring.SourceCollider
                : authoring.GetComponent<BoxCollider>();

            if (!box)
            {
                Debug.LogWarning($"[CharacterBoxAuthoring] {authoring.name} 上没找到 BoxCollider");
                return;
            }

            // 碰撞查询使用半尺寸 因此烘焙时统一完成换算
            Vector3 size = box.size;

            AddComponent(entity, new CharacterBoxInfo
            {
                Center      = box.center,
                HalfExtents = (float3)(size * 0.5f)
            });
        }
    }
}
