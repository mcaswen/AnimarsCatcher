using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Player
{
    /// <summary>
    /// 运行时角色盒体的本地中心和半尺寸
    /// </summary>
    public struct CharacterBoxGeometry : IComponentData
    {
        // 相对 Entity 原点的本地偏移
        public float3 Center;
        public float3 HalfExtents;
    }

    /// <summary>
    /// 从场景 BoxCollider 烘焙 ECS 盒体尺寸
    /// </summary>
    [DisallowMultipleComponent]
    [MovedFrom(true, "AnimarsCatcher.Player", "AnimarsCatcher.Player", "CharacterBoxAuthoring")]
    public class CharacterBoxGeometryAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("SourceCollider")]
        [SerializeField] private BoxCollider _sourceCollider;

        private sealed class Baker : Baker<CharacterBoxGeometryAuthoring>
        {
            public override void Bake(CharacterBoxGeometryAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                var box = authoring._sourceCollider != null
                    ? authoring._sourceCollider
                    : authoring.GetComponent<BoxCollider>();

                if (!box)
                {
                    Debug.LogWarning($"[CharacterBoxGeometryAuthoring] {authoring.name} 上没找到 BoxCollider");
                    return;
                }

                // 碰撞查询使用半尺寸，因此烘焙时统一完成换算
                Vector3 size = box.size;

                AddComponent(entity, new CharacterBoxGeometry
                {
                    Center = box.center,
                    HalfExtents = (float3)(size * 0.5f)
                });
            }
        }
    }
}
