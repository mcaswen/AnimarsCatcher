using Unity.Entities;
using UnityEngine;

[DisallowMultipleComponent]
public class MovementRaycastContextRegistry : MonoBehaviour
{
    class Baker : Baker<MovementRaycastContextRegistry>
    {
        public override void Bake(MovementRaycastContextRegistry authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.None);

            AddComponent<MovementClickRequest>(entity);
            AddComponent<MovementClickResult>(entity);
            AddComponent<MovementClickProcessedVersion>(entity);
        }
    }
}
