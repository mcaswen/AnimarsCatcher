using Unity.Entities;
using UnityEngine;

public class GlobalGameResourceStateAuthoring : MonoBehaviour
{
    public int initialTimeSeconds;

    class Baker : Baker<GlobalGameResourceStateAuthoring>
    {
        public override void Bake(GlobalGameResourceStateAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);

            AddComponent(entity, new GlobalGameResourceState
            {
                MatchTimeSeconds = authoring.initialTimeSeconds
            });

            AddComponent<GlobalGameResourceTag>(entity);
        }
    }
}
