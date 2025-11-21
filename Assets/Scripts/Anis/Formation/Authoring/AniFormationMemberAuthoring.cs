using Unity.Entities;
using UnityEngine;

public class AniFormationMemberAuthoring : MonoBehaviour
{
    public Entity leader;
    public int slotIndex = 0;
}

public class AniFormationMemberBaker : Baker<AniFormationMemberAuthoring>
{
    public override void Bake(AniFormationMemberAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.Dynamic);

        AddComponent(entity, new AniFormationMember
        {
            leader = authoring.leader,
            slotIndex = authoring.slotIndex
        });
    }
}