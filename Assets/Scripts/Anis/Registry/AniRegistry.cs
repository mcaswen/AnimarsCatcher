using Unity.Entities;
using UnityEngine;

public class AniRegistry : MonoBehaviour
{
    public GameObject BlasterAniGhostPrefab;
    public GameObject PickerAniGhostPrefab;

    class Baker : Baker<AniRegistry>
    {
        public override void Bake(AniRegistry authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            var blasterPrefabEntity = GetEntity(authoring.BlasterAniGhostPrefab, TransformUsageFlags.Dynamic);
            var pickerPrefabEntity = GetEntity(authoring.PickerAniGhostPrefab, TransformUsageFlags.Dynamic);

            AddComponent(entity, new AniGhostPrefabCollection
            {
                BlasterAniPrefabEntity = blasterPrefabEntity,
                PickerAniPrefabEntity = pickerPrefabEntity
            });
        }
    }
}

public struct AniGhostPrefabCollection : IComponentData
{
    public Entity BlasterAniPrefabEntity;
    public Entity PickerAniPrefabEntity;
}
