using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;
using Material = Unity.Physics.Material;
using TerrainCollider = Unity.Physics.TerrainCollider;


public class TerrainColliderBaker : Baker<TerrainColliderAuthoring>
{
    public override void Bake(TerrainColliderAuthoring authoring)
    {
        if (authoring.terrain == null)
        {
            Debug.LogError("TerrainColliderAuthoring requires a Terrain component to function", authoring);
            return;
        }

        var terrain = authoring.terrain;

        DependsOn(terrain.terrainData);
        var terrainData = terrain.terrainData;

        int resolution = terrainData.heightmapResolution;
        var size = new int2(resolution, resolution);

        // 利用 TerrainData.size 推导采样间距
        float3 scale = new float3(
            terrainData.size.x / (resolution - 1),
            terrainData.size.y,
            terrainData.size.z / (resolution - 1));

        // 读取高度图数据
        var source = terrainData.GetHeights(0, 0, resolution, resolution);   // src[z, x] ∈ [0,1]
        var colliderHeights = new NativeArray<float>(resolution * resolution, Allocator.Temp);
        for (int z = 0; z < resolution; z++)
            for (int x = 0; x < resolution; x++)
                colliderHeights[x + z * resolution] = source[z, x];


        // 应用物理模板
        var template = authoring.physicsTemplate;

        var filter = new CollisionFilter
        {
            BelongsTo = template.BelongsTo.Value,
            CollidesWith = template.CollidesWith.Value,
        };

        var material = new Material
        {
            FrictionCombinePolicy = template.Friction.CombineMode,
            RestitutionCombinePolicy = template.Restitution.CombineMode,
            CustomTags = template.CustomTags.Value,
            Friction = template.Friction.Value,
            Restitution = template.Restitution.Value,
            CollisionResponse = template.CollisionResponse,
            EnableMassFactors = false,
            EnableSurfaceVelocity = false
        };

        const TerrainCollider.CollisionMethod collisionMethod = TerrainCollider.CollisionMethod.Triangles;
        var collider = new PhysicsCollider
        {
            Value = TerrainCollider.Create(colliderHeights, size, scale, collisionMethod, filter, material)
        };

        AddBlobAsset(ref collider.Value, out _);

        var entity = GetEntity(TransformUsageFlags.Dynamic);
        AddComponent(entity, collider);
        AddSharedComponent(entity, new PhysicsWorldIndex());
        AddBuffer<PhysicsColliderKeyEntityPair>(entity);

        colliderHeights.Dispose();
    }
}