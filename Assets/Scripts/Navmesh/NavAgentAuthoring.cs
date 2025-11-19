using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

[DisallowMultipleComponent]
public class NavAgentAuthoring : MonoBehaviour
{
    [Header("NavAgent Config")]
    public float Speed = 3.5f;
    public float StoppingDistance = 0.5f;
    public Transform[] InitialWaypoints;
}

public class NavAgentBaker : Baker<NavAgentAuthoring>
{
    public override void Bake(NavAgentAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.Dynamic);

        AddComponent(entity, new NavAgent
        {
            Speed = authoring.Speed,
            StoppingDistance = authoring.StoppingDistance,
            LastHandledNavRequestVersion = -1, 
            CurrentWaypointIndex = -1         
        });

        AddComponent(entity, new NavSteering
        {
            SteeringTarget = float3.zero,
            PathVersion = 0,
            HasPath = 0
        });

        var buf = AddBuffer<NavWaypoint>(entity);

        if (authoring.InitialWaypoints != null && authoring.InitialWaypoints.Length > 0)
        {
            for (int i = 0; i < authoring.InitialWaypoints.Length; i++)
            {
                var t = authoring.InitialWaypoints[i];
                if (t != null)
                {
                    buf.Add(new NavWaypoint { Position = t.position });
                }
            }
        }
    }
}
