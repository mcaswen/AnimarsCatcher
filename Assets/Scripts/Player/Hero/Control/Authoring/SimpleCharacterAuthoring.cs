using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Unity.NetCode;

/// <summary>配置简化角色的移动和碰撞参数</summary>
[DisallowMultipleComponent]
public class SimpleCharacterAuthoring : MonoBehaviour
{
    public float MoveSpeed = 6f;
    public float RotationSharpness = 15f;
    public float ColliderHeight = 1.8f;
    public float ColliderRadius = 0.4f;

    /// <summary>负责创建简化角色的预测组件</summary>
    class Baker : Baker<SimpleCharacterAuthoring>
    {
        /// <summary>烘焙简化角色配置、输入缓冲和预测标记</summary>
        /// <param name="authoring">简化角色 Authoring 配置</param>
        public override void Bake(SimpleCharacterAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent(entity, new SimpleCharacter
            {
                MoveSpeed        = authoring.MoveSpeed,
                RotationSharpness = authoring.RotationSharpness,
                ColliderHeight   = authoring.ColliderHeight,
                ColliderRadius   = authoring.ColliderRadius,
            });

            AddComponent(entity, new SimpleCharacterControl
            {
                MoveVector = float3.zero
            });

            AddComponent<PredictedGhost>(entity);
            AddComponent<CharacterTag>(entity);
            AddComponent<Simulate>(entity);

            AddBuffer<InputCommand>(entity);
        }
    }
}
