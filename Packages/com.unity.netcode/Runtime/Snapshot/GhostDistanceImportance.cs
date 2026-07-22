using System;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    /// 将此组件添加到每个连接，用于确定该连接应优先处理哪些 Tile
    /// 此组件会作为参数传入内置缩放函数以计算 Importance
    /// 参见 <see cref="GhostDistanceImportance"/> 的实现
    /// </summary>
    public struct GhostConnectionPosition : IComponentData
    {
        /// <summary>
        /// Tile 在世界坐标中的位置
        /// </summary>
        public float3 Position;
        /// <summary>
        /// 当前没有系统更新此值，供自定义 Importance 实现使用
        /// </summary>
        public quaternion Rotation;
        /// <summary>
        /// 当前没有系统更新此值，供自定义 Importance 实现使用
        /// </summary>
        public float4 ViewSize;
    }

    /// <summary>
    /// <see cref="GhostImportance"/> 的默认配置数据
    /// 通过 Tile 将实体分组到空间 Chunk 中，并由 <see cref="GhostDistancePartitioningSystem"/>
    /// 根据距离设置 Chunk 优先级，从而高效实现基于距离的 Importance 缩放
    /// </summary>
    public struct GhostDistanceData : IComponentData
    {
        /// <summary>
        /// Tile 的尺寸
        /// </summary>
        public int3 TileSize;
        /// <summary>
        /// Tile 中心的偏移量
        /// </summary>
        public int3 TileCenter;
        /// <summary>
        /// 用于优化，表示每个 Tile 正负两个方向上的边界宽度
        /// 判断实体是否移动到另一个 Tile 时，会将此边界值作为额外的距离阈值
        /// 从而降低频繁在小范围内移动的 Ghost 触发昂贵结构变更的频率
        /// </summary>
        public float3 TileBorderWidth;
    }

    /// <summary>
    /// <see cref="GhostImportance"/> API 的默认实现，用于计算基于距离的 Importance 缩放系数
    /// 距离客户端 Importance 焦点较远的实体会降低发送频率，焦点由 <see cref="GhostConnectionPosition"/> 指定
    /// 延伸阅读：https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/optimizations.html#importance-scaling
    /// </summary>
    [BurstCompile]
    public struct GhostDistanceImportance
    {
        /// <summary>
        /// 指向 <see cref="BatchScale"/> 静态方法的指针
        /// </summary>
        public static readonly PortableFunctionPointer<GhostImportance.BatchScaleImportanceDelegate> BatchScaleFunctionPointer =
            new PortableFunctionPointer<GhostImportance.BatchScaleImportanceDelegate>(BatchScale);
        /// <summary>
        /// 指向 <see cref="BatchScaleWithRelevancy"/> 静态方法的指针
        /// </summary>
        public static readonly PortableFunctionPointer<GhostImportance.BatchScaleImportanceDelegate> BatchScaleWithRelevancyFunctionPointer =
            new PortableFunctionPointer<GhostImportance.BatchScaleImportanceDelegate>(BatchScaleWithRelevancy);

        /// <summary>
        /// 指向 <see cref="CalculateDefaultScaledPriority"/> 静态方法的指针
        /// </summary>
#pragma warning disable CS0618 // 类型或成员已过时
        public static readonly PortableFunctionPointer<GhostImportance.ScaleImportanceDelegate> ScaleFunctionPointer =
            new PortableFunctionPointer<GhostImportance.ScaleImportanceDelegate>(Scale);
#pragma warning restore CS0618 // 类型或成员已过时

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(GhostImportance.ScaleImportanceDelegate))]
        [Obsolete("Prefer `BatchScale` as it significantly reduces the total number of function pointer calls. RemoveAfter 1.x")]
        private static int Scale(IntPtr connectionDataPtr, IntPtr distanceDataPtr, IntPtr chunkTilePtr, int basePriority)
        {
            var distanceData = GhostComponentSerializer.TypeCast<GhostDistanceData>(distanceDataPtr);
            var centerTile = GhostDistancePartitioningSystem.CalculateTile(in distanceData, in GhostComponentSerializer.TypeCast<GhostConnectionPosition>(connectionDataPtr).Position);
            var chunkTile = GhostComponentSerializer.TypeCast<GhostDistancePartitionShared>(chunkTilePtr);
            return CalculateDefaultScaledPriority(basePriority, chunkTile, centerTile);
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(GhostImportance.BatchScaleImportanceDelegate))]
        private static unsafe void BatchScale(IntPtr connectionDataPtr, IntPtr distanceDataPtr, IntPtr sharedComponentTypeHandlePtr,
            ref UnsafeList<PrioChunk> chunks)
        {
            var distanceData = GhostComponentSerializer.TypeCast<GhostDistanceData>(distanceDataPtr);
            var centerTile = (int3)((GhostComponentSerializer.TypeCast<GhostConnectionPosition>(connectionDataPtr).Position - distanceData.TileCenter) / distanceData.TileSize);
            var sharedType = GhostComponentSerializer.TypeCast<DynamicSharedComponentTypeHandle>(sharedComponentTypeHandlePtr);
            for (int i = 0; i < chunks.Length; ++i)
            {
                ref var data = ref chunks.ElementAt(i);
                if (!data.chunk.Has(ref sharedType)) continue;
                var chunkTile = (GhostDistancePartitionShared*)data.chunk.GetDynamicSharedComponentDataAddress(ref sharedType);
                data.priority = CalculateDefaultScaledPriority(data.priority, in *chunkTile, centerTile);
            }
        }

        /// <summary>
        /// 距离缩放函数的默认实现
        /// </summary>
        /// <param name="priority">基础优先级</param>
        /// <param name="chunkTile">Chunk 所在 Tile</param>
        /// <param name="centerTile">中心 Tile</param>
        /// <returns>缩放后的优先级</returns>
        public static int CalculateDefaultScaledPriority(int priority, in GhostDistancePartitionShared chunkTile, in int3 centerTile)
        {
            var delta = chunkTile.Index - centerTile;
            var distSq = math.dot(delta, delta);

            // 平方距离 3 覆盖三维空间中所有相邻 Tile，避免连接靠近 Tile 边界时错误降低相邻 Tile 的优先级
            if (distSq > 3)
                priority /= distSq;

            return priority;
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(GhostImportance.BatchScaleImportanceDelegate))]
        private static unsafe void BatchScaleWithRelevancy(IntPtr connectionDataPtr, IntPtr distanceDataPtr, IntPtr sharedComponentTypeHandlePtr,
            ref UnsafeList<PrioChunk> chunks)
        {
            var distanceData = GhostComponentSerializer.TypeCast<GhostDistanceData>(distanceDataPtr);
            var centerTile = (int3)((GhostComponentSerializer.TypeCast<GhostConnectionPosition>(connectionDataPtr).Position - distanceData.TileCenter) / distanceData.TileSize);
            var sharedType = GhostComponentSerializer.TypeCast<DynamicSharedComponentTypeHandle>(sharedComponentTypeHandlePtr);
            for (int i = 0; i < chunks.Length ; ++i)
            {
                ref var data = ref chunks.ElementAt(i);
                var basePriority = data.priority;
                if (data.chunk.Has(ref sharedType))
                {
                    var chunkTile = (GhostDistancePartitionShared*) data.chunk.GetDynamicSharedComponentDataAddress(ref sharedType);
                    var delta = chunkTile->Index - centerTile;
                    var distSq = math.dot(delta, delta);
                    basePriority *= 1000;
                    // 平方距离 3 覆盖三维空间中所有相邻 Tile，避免连接靠近 Tile 边界时错误降低相邻 Tile 的优先级
                    basePriority = math.select(basePriority, basePriority / math.max(1, distSq), distSq > 3);
                    data.priority = basePriority;
                    // 与玩家的 Tile 距离超过 4 的 Chunk 均视为不相关，除非显式加入 GhostRelevancySet
                    data.isRelevant = distSq <= 16;
                }
            }
        }
    }
}
