using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;
using Material = Unity.Physics.Material;
using TerrainCollider = Unity.Physics.TerrainCollider;

namespace AnimarsCatcher.Physics.Authoring
{
    /// <summary>
    /// 将 Unity Terrain 高度图烘焙为 Unity Physics 地形碰撞体
    /// </summary>
    public sealed class TerrainColliderBaker : Baker<TerrainColliderAuthoring>
    {
        public override void Bake(TerrainColliderAuthoring authoring)
        {
            if (authoring.Terrain == null)
            {
                Debug.LogError("TerrainColliderAuthoring requires a Terrain component to function", authoring);
                return;
            }

            var terrain = authoring.Terrain;

            // 高度图或尺寸变化时让 Baker 自动失效并重新生成 Collider Blob
            DependsOn(terrain.terrainData);
            var terrainData = terrain.terrainData;

            int resolution = terrainData.heightmapResolution;
            var size = new int2(resolution, resolution);

            // 依据地形尺寸和高度图分辨率计算各轴采样间距
            float3 scale = new float3(
                terrainData.size.x / (resolution - 1),
                terrainData.size.y,
                terrainData.size.z / (resolution - 1));

            // GetHeights 返回零到一的归一化高度，Y 轴 Scale 在 Collider 中恢复实际高度
            var source = terrainData.GetHeights(0, 0, resolution, resolution);
            var colliderHeights = new NativeArray<float>(resolution * resolution, Allocator.Temp);
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    colliderHeights[x + z * resolution] = source[z, x];
                }
            }

            // 沿用项目物理模板，保持碰撞层和表面属性一致
            var template = authoring.PhysicsMaterialTemplate;
            var filter = new CollisionFilter
            {
                BelongsTo = template.BelongsTo.Value,
                CollidesWith = template.CollidesWith.Value
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
            // Create 在返回前复制高度数据，临时数组可在本次 Bake 末尾释放
            var collider = new PhysicsCollider
            {
                Value = TerrainCollider.Create(colliderHeights, size, scale, collisionMethod, filter, material)
            };

            // 注册 Blob 资产以便烘焙缓存和 Entity 间复用
            AddBlobAsset(ref collider.Value, out _);

            // Dynamic Transform 保留场景 Terrain 的位置变换，不代表碰撞体会参与动力学
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, collider);
            // 将地形加入默认 Physics World，并建立复合碰撞键到 Entity 的映射缓冲区
            AddSharedComponent(entity, new PhysicsWorldIndex());
            AddBuffer<PhysicsColliderKeyEntityPair>(entity);

            colliderHeights.Dispose();
        }
    }
}
