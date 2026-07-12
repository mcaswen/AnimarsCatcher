using UnityEngine;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine.Serialization;

public struct PlayerTag : IComponentData {}

[DisallowMultipleComponent]
public class ThirdPersonPlayerControlAuthoring : MonoBehaviour
{
    [FormerlySerializedAs("controlledCamera")]
    [SerializeField] private GameObject _controlledCamera;

    public class Baker : Baker<ThirdPersonPlayerControlAuthoring>
    {
        public override void Bake(ThirdPersonPlayerControlAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new ThirdPersonPlayerControl
            {
                ControlledCamera = GetEntity(authoring._controlledCamera, TransformUsageFlags.Dynamic)
            });

            AddComponent<PlayerInput>(entity);
            AddComponent<PlayerTag>(entity);
        }
    }
}
