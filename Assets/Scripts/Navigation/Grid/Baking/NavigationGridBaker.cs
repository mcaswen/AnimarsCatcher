using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 将可检查的 Grid 烘焙资产转换为运行时只读 Blob
    /// </summary>
    public sealed class NavigationGridBaker : Baker<NavigationGridAuthoring>
    {
        public override void Bake(NavigationGridAuthoring authoring)
        {
            // Baker 只消费已经持久化且结构有效的资产
            // 场景新鲜度由编辑器构建门禁验证 Runtime 不反向调用 Editor
            NavigationGridBakeAsset bakeAsset = authoring.BakeAsset;
            if (bakeAsset == null)
            {
                Debug.LogError("NavigationGridAuthoring requires a bake asset", authoring);
                return;
            }

            DependsOn(bakeAsset);
            if (!bakeAsset.IsUsable)
            {
                Debug.LogError("Navigation Grid bake asset is missing or uses an unsupported version", bakeAsset);
                return;
            }

            // Blob 字段顺序镜像 Bake Asset 的运行时只读契约
            // Cell 按行主序复制使运行时索引和编辑器检查保持一致
            var builder = new BlobBuilder(Allocator.Temp);
            ref NavigationGridBlob root = ref builder.ConstructRoot<NavigationGridBlob>();
            Bounds bounds = bakeAsset.WorldBounds;

            root.BoundsMinimum = new float3(bounds.min.x, bounds.min.y, bounds.min.z);
            root.BoundsMaximum = new float3(bounds.max.x, bounds.max.y, bounds.max.z);
            root.CellSize = bakeAsset.CellSize;
            root.BaseAgentRadius = bakeAsset.BaseAgentRadius;
            root.BaseAgentHeight = bakeAsset.BaseAgentHeight;
            root.Width = bakeAsset.Width;
            root.Height = bakeAsset.Height;
            root.ClusterSizeInCells = bakeAsset.ClusterSizeInCells;
            root.RegionCount = bakeAsset.RegionCount;
            root.DataVersion = bakeAsset.DataVersion;
            root.GeometryHash = new Unity.Entities.Hash128(bakeAsset.GeometryHash);
            root.ParameterHash = new Unity.Entities.Hash128(bakeAsset.ParameterHash);
            root.DataHash = new Unity.Entities.Hash128(bakeAsset.DataHash);

            BlobBuilderArray<NavigationGridCell> blobCells =
                builder.Allocate(ref root.Cells, bakeAsset.CellCount);

            for (int i = 0; i < bakeAsset.CellCount; i++)
            {
                NavigationGridCellData source = bakeAsset.GetCell(i);
                blobCells[i] = new NavigationGridCell
                {
                    Height = source.Height,
                    SurfaceNormal = new float3(
                        source.SurfaceNormal.x,
                        source.SurfaceNormal.y,
                        source.SurfaceNormal.z),
                    SlopeDegrees = source.SlopeDegrees,
                    TerrainCost = source.TerrainCost,
                    Clearance = source.Clearance,
                    RegionId = source.RegionId,
                    ClusterId = source.ClusterId,
                    NeighborMask = (byte)source.NeighborMask,
                    Walkable = source.Walkable ? (byte)1 : (byte)0,
                };
            }

            BlobAssetReference<NavigationGridBlob> blobReference =
                builder.CreateBlobAssetReference<NavigationGridBlob>(Allocator.Persistent);
            builder.Dispose();

            // 交给 Baker 按完整 Blob 内容去重，同时正确释放重复构建的临时实例
            AddBlobAsset(ref blobReference, out _);

            Entity entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new NavigationGridReference { Value = blobReference });
        }
    }
}
