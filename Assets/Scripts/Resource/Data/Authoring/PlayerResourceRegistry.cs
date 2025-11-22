using Unity.Entities;
using UnityEngine;

public class PlayerResourceRegistry : MonoBehaviour
{
    public GameObject PlayerResourceGhostPrefab;

    class Baker : Baker<PlayerResourceRegistry>
    {
        public override void Bake(PlayerResourceRegistry authoring)
        {
            var holderEntity = GetEntity(TransformUsageFlags.None);
            var prefabEntity = GetEntity(authoring.PlayerResourceGhostPrefab, TransformUsageFlags.Dynamic);

            AddComponent(holderEntity, new PlayerResourceGhostPrefab
            {
                Value = prefabEntity
            });
        }
    }
}

public struct PlayerResourceGhostPrefab : IComponentData
{
    public Entity Value;
}
