using UnityEngine;
using Unity.Entities;
using Unity.NetCode;

public struct PlayerTag : IComponentData {}

[DisallowMultipleComponent]
public class ThirdPersonPlayerControlAuthoring : MonoBehaviour
{
    public class Baker : Baker<ThirdPersonPlayerControlAuthoring>
    {
        public override void Bake(ThirdPersonPlayerControlAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new ThirdPersonPlayerControl
            { });

            AddComponent<PlayerInput>(entity);
            AddComponent<PlayerTag>(entity);
        }
    }
}