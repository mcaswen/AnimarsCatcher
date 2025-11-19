using Unity.Entities;
using UnityEngine;

public class AniFormationLeaderAuthoring : MonoBehaviour
{
    public int columnCount = 5;
    public float horizontalSpacing = 1f;
    public float backwardSpacing = 3.5f;
    public float arrivalRadius = 0.5f;
}

public class AniFormationLeaderBaker : Baker<AniFormationLeaderAuthoring>
{
    public override void Bake(AniFormationLeaderAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.Dynamic);

        AddComponent(entity, new AniFormationLeader
        {
            columnCount = authoring.columnCount,
            horizontalSpacing = authoring.horizontalSpacing,
            backwardSpacing = authoring.backwardSpacing,
            arrivalRadius = authoring.arrivalRadius
        });

        AddBuffer<AniFormationRoster>(entity);
    }
}