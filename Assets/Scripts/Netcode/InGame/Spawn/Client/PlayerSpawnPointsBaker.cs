using Unity.Entities;
using UnityEngine;

public class PlayerSpawnPointsBaker : Baker<PlayerSpawnPointsAuthoring>
{
    public override void Bake(PlayerSpawnPointsAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.None);

        AddComponent<PlayerSpawnPointsTag>(entity);
        AddComponent(entity, new PlayerSpawnPointsState { NextIndex = 0 });
        AddComponent(entity, new PlayerSpawnSelectMode { Value = authoring.selectMode });

        var buffer = AddBuffer<PlayerSpawnPointElement>(entity);

        var points = authoring.gameObject.GetComponentsInChildren<Transform>();
        foreach (var point in points)
        {
            if (point == authoring.transform) continue;

            buffer.Add(new PlayerSpawnPointElement
            {
                Position = point.position,
                Rotation = point.rotation
            });
        }
    }
}