using Unity.Entities;
using UnityEngine;

public class BlasterAniGhostPrefabAuthoring : MonoBehaviour
{
    public GameObject BlasterAniGhostPrefab;
    public GameObject PickerAniGhostPrefab;

    class Baker : Baker<BlasterAniGhostPrefabAuthoring>
    {
        public override void Bake(BlasterAniGhostPrefabAuthoring authoring)
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
