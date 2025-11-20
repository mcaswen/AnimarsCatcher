using UnityEngine;
using Unity.Entities;
using Unity.NetCode;

public struct PlayerTag : IComponentData {}

[DisallowMultipleComponent]
public class ThirdPersonPlayerControlAuthoring : MonoBehaviour
{
    [SerializeField] private GameObject controlledCamera;

    public class Baker : Baker<ThirdPersonPlayerControlAuthoring>
    {
        public override void Bake(ThirdPersonPlayerControlAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new ThirdPersonPlayerControl
            {
                ControlledCamera = GetEntity(authoring.controlledCamera, TransformUsageFlags.Dynamic)
            });

            AddComponent<PlayerInput>(entity);
            AddComponent<PlayerTag>(entity);
        }
    }
}