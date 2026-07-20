namespace AnimarsCatcher.Player
{
    using Unity.Entities;
    using Unity.Mathematics;
    using UnityEngine;
    using UnityEngine.Serialization;
    using Unity.NetCode;

    /// <summary>
    /// 配置简化角色的移动和碰撞参数
    /// </summary>
    [DisallowMultipleComponent]
    public class SimpleCharacterAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("MoveSpeed")]
        [SerializeField] private float _moveSpeed = 6f;
        [FormerlySerializedAs("RotationSharpness")]
        [SerializeField] private float _rotationSharpness = 15f;
        [FormerlySerializedAs("ColliderHeight")]
        [SerializeField] private float _colliderHeight = 1.8f;
        [FormerlySerializedAs("ColliderRadius")]
        [SerializeField] private float _colliderRadius = 0.4f;

        private sealed class Baker : Baker<SimpleCharacterAuthoring>
        {
            public override void Bake(SimpleCharacterAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponent(entity, new SimpleCharacter
                {
                    MoveSpeed        = authoring._moveSpeed,
                    RotationSharpness = authoring._rotationSharpness,
                    ColliderHeight   = authoring._colliderHeight,
                    ColliderRadius   = authoring._colliderRadius,
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
}
