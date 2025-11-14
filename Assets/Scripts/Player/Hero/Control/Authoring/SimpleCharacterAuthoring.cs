using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Unity.NetCode;

[DisallowMultipleComponent]
public class SimpleCharacterAuthoring : MonoBehaviour
{
    public float MoveSpeed = 6f;
    public float RotationSharpness = 15f;
    public float ColliderHeight = 1.8f;
    public float ColliderRadius = 0.4f;

    class Baker : Baker<SimpleCharacterAuthoring>
    {
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