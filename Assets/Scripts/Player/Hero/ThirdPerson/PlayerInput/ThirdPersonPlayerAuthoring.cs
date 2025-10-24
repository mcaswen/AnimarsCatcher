using UnityEngine;
using Unity.Entities;
using Unity.NetCode;

public struct PlayerTag : IComponentData {}

[DisallowMultipleComponent]
public class ThirdPersonPlayerAuthoring : MonoBehaviour
{
    public GameObject ControlledCamera;

    public class Baker : Baker<ThirdPersonPlayerAuthoring>
    {
        public override void Bake(ThirdPersonPlayerAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new ThirdPersonPlayer
            {
                ControlledCamera = GetEntity(authoring.ControlledCamera, TransformUsageFlags.Dynamic),
            });

            AddComponent<ThirdPersonPlayerInputs>(entity);
            AddComponent<PlayerTag>(entity);
        }
    }
}