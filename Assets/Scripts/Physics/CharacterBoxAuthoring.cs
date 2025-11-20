using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public struct CharacterBoxInfo : IComponentData
{
    public float3 Center; // 本地偏移
    public float3 HalfExtents; // 一半的长宽高
}

[DisallowMultipleComponent]
public class CharacterBoxAuthoring : MonoBehaviour
{
    public BoxCollider SourceCollider;

    class Baker : Baker<CharacterBoxAuthoring>
    {
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

            Vector3 size = box.size;

            AddComponent(entity, new CharacterBoxInfo
            {
                Center      = box.center,
                HalfExtents = (float3)(size * 0.5f)
            });
        }
    }
}
