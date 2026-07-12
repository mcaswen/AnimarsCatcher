using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;
using Material = Unity.Physics.Material;
using TerrainCollider = Unity.Physics.TerrainCollider;


/// <summary>
/// 将 Unity Terrain 高度图烘焙为 Unity Physics 地形碰撞体
/// </summary>
public class TerrainColliderBaker : Baker<TerrainColliderAuthoring>
{
    /// <summary>
    /// 读取高度图 物理过滤器和材质并创建 Blob 碰撞体
    /// </summary>
    /// <param name="authoring">地形碰撞烘焙配置</param>
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

        // 依据地形尺寸和高度图分辨率计算各轴采样间距
        float3 scale = new float3(
            terrainData.size.x / (resolution - 1),
            terrainData.size.y,
            terrainData.size.z / (resolution - 1));

        // Unity 返回的数据按行列组织 转成碰撞体要求的一维高度数组
        var source = terrainData.GetHeights(0, 0, resolution, resolution);
        var colliderHeights = new NativeArray<float>(resolution * resolution, Allocator.Temp);
        for (int z = 0; z < resolution; z++)
            for (int x = 0; x < resolution; x++)
                colliderHeights[x + z * resolution] = source[z, x];


        // 沿用项目物理模板 保持碰撞层和表面属性一致
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

        // 注册 Blob 资产以便烘焙缓存和实体间复用
        AddBlobAsset(ref collider.Value, out _);

        var entity = GetEntity(TransformUsageFlags.Dynamic);
        AddComponent(entity, collider);
        AddSharedComponent(entity, new PhysicsWorldIndex());
        AddBuffer<PhysicsColliderKeyEntityPair>(entity);

        colliderHeights.Dispose();
    }
}
