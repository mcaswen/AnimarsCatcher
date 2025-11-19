using Unity.Entities;
using UnityEngine;

public class PlayerResourceGhostAuthoring : MonoBehaviour
{
    class Baker : Baker<PlayerResourceGhostAuthoring>
    {
        public override void Bake(PlayerResourceGhostAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);

            AddComponent<PlayerResourceTag>(entity);
            AddComponent<PlayerResourceState>(entity);
        }
    }
}
