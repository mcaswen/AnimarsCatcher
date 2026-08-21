using AnimarsCatcher.Core;
using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 提供世界坐标与格子之间的转换、端点纠正和格子直线检查
    /// </summary>
    public static class NavigationGridQuery
    {
        private const float CostEpsilon = 0.00001f;
        private const float MinimumTerrainCost = 0.01f;

        // 只有导航网格范围内的世界坐标才能直接转换，范围外的位置不会被悄悄夹到边缘
        // 纠正端点时依次比较距离、地形成本、可用空间和格子索引
        // 端点纠正和直线检查使用同一套通行规则，包括动态障碍
        // 格子直线采用整数步进，避免不同平台的浮点舍入改变经过的格子
        // 查询方法只返回普通值或索引，不持有 Native 容器，也不管理 ECS 生命周期

        public static bool CanAgentOccupy(
            ref NavigationGridBlob grid,
            int cellIndex,
            float agentRadius,
            float clearanceMargin)
        {
            return NavigationGridTraversal.CanAgentOccupy(
                ref grid, cellIndex, agentRadius, clearanceMargin);
        }
        public static bool TryWorldToCell(
            ref NavigationGridBlob grid,
            float3 worldPosition,
            out int2 coordinate,
            out int cellIndex)
        {
            coordinate = default;
            cellIndex = -1;
            if (!NavigationGridTraversal.IsGridShapeValid(ref grid) || !VectorMath.IsFinite(worldPosition))
            {
                return false;
            }

            float2 localPosition = new float2(
                worldPosition.x - grid.BoundsMinimum.x,
                worldPosition.z - grid.BoundsMinimum.z);
            // 世界范围采用左闭右开区间，最大边界不会被错误换算成 Width 或 Height
            if (localPosition.x < 0f || localPosition.y < 0f ||
                localPosition.x >= grid.Width * grid.CellSize ||
                localPosition.y >= grid.Height * grid.CellSize)
            {
                return false;
            }

            coordinate = new int2(
                (int)math.floor(localPosition.x / grid.CellSize),
                (int)math.floor(localPosition.y / grid.CellSize));
            cellIndex = coordinate.x + coordinate.y * grid.Width;
            return true;
        }

        /// <summary>
        /// 将格子索引转换为该格子中心在烘焙地面上的世界坐标
        /// </summary>
        /// <param name="grid">运行时只读 Grid</param>
        /// <param name="cellIndex">行主序 Cell 索引</param>
        /// <returns>目标 Cell 中心的世界坐标</returns>
        public static float3 GetCellWorldPosition(
            ref NavigationGridBlob grid,
            int cellIndex)
        {
            int x = cellIndex % grid.Width;
            int z = cellIndex / grid.Width;
            NavigationGridCell cell = grid.Cells[cellIndex];
            return new float3(
                grid.BoundsMinimum.x + (x + 0.5f) * grid.CellSize,
                cell.Height,
                grid.BoundsMinimum.z + (z + 0.5f) * grid.CellSize);
        }

        /// <summary>
        /// 判断指定体型的角色能否安全站在目标格子中
        /// </summary>
        /// <param name="grid">运行时只读 Grid</param>
        /// <param name="cellIndex">待检查 Cell 索引</param>
        /// <param name="agentRadius">Agent 世界半径</param>
        /// <param name="clearanceMargin">额外安全边距</param>
        /// <returns>格子本身可行走且周围空间足够时返回 true</returns>
        public static bool TryProjectToNearestCell(
            ref NavigationGridBlob grid,
            float3 worldPosition,
            float agentRadius,
            float clearanceMargin,
            int maximumRadiusInCells,
            out int projectedCellIndex)
        {
            return TryProjectToNearestCell(
                ref grid,
                worldPosition,
                agentRadius,
                clearanceMargin,
                maximumRadiusInCells,
                default,
                out projectedCellIndex);
        }

        public static bool TryProjectToNearestCell(
            ref NavigationGridBlob grid,
            float3 worldPosition,
            float agentRadius,
            float clearanceMargin,
            int maximumRadiusInCells,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay,
            out int projectedCellIndex)
        {
            projectedCellIndex = -1;
            if (!NavigationGridTraversal.IsGridShapeValid(ref grid) ||
                !VectorMath.IsFinite(worldPosition) ||
                !math.isfinite(agentRadius) ||
                !math.isfinite(clearanceMargin) ||
                agentRadius < 0f ||
                clearanceMargin < 0f ||
                maximumRadiusInCells < 0)
            {
                return false;
            }

            // 原始坐标不先夹到地图边缘，这样地图外目标仍会受到最大搜索半径限制
            int rawX = (int)math.floor(
                (worldPosition.x - grid.BoundsMinimum.x) / grid.CellSize);
            int rawZ = (int)math.floor(
                (worldPosition.z - grid.BoundsMinimum.z) / grid.CellSize);
            int minimumX = math.max(0, rawX - maximumRadiusInCells);
            int maximumX = math.min(grid.Width - 1, rawX + maximumRadiusInCells);
            int minimumZ = math.max(0, rawZ - maximumRadiusInCells);
            int maximumZ = math.min(grid.Height - 1, rawZ + maximumRadiusInCells);

            // 候选格子按距离、地形成本、可用空间和索引排序，不依赖扫描顺序
            float bestDistanceSquared = float.PositiveInfinity;
            float bestTerrainCost = float.PositiveInfinity;
            float bestClearance = float.NegativeInfinity;

            // 扫描整个候选范围后再选择，避免先遇到的角落格子意外胜出
            for (int z = minimumZ; z <= maximumZ; z++)
            {
                for (int x = minimumX; x <= maximumX; x++)
                {
                    int cellIndex = x + z * grid.Width;
                    if (!NavigationGridTraversal.CanAgentOccupyDynamic(
                            ref grid,
                            cellIndex,
                            agentRadius,
                            clearanceMargin,
                            dynamicOverlay))
                    {
                        continue;
                    }

                    NavigationGridCell cell = grid.Cells[cellIndex];
                    float3 cellPosition = GetCellWorldPosition(ref grid, cellIndex);
                    float2 offset = new float2(
                        cellPosition.x - worldPosition.x,
                        cellPosition.z - worldPosition.z);
                    float distanceSquared = math.lengthsq(offset);
                    float terrainCost = math.max(MinimumTerrainCost, cell.TerrainCost);
                    float effectiveClearance = cell.Clearance;
                    if (dynamicOverlay.IsCreated && cellIndex < dynamicOverlay.Length)
                    {
                        effectiveClearance = math.max(
                            0f,
                            effectiveClearance -
                            math.max(0f, dynamicOverlay[cellIndex].ClearanceReduction));
                    }
                    if (IsBetterProjectionCandidate(
                            distanceSquared,
                            terrainCost,
                            effectiveClearance,
                            cellIndex,
                            bestDistanceSquared,
                            bestTerrainCost,
                            bestClearance,
                            projectedCellIndex))
                    {
                        bestDistanceSquared = distanceSquared;
                        bestTerrainCost = terrainCost;
                        bestClearance = cell.Clearance;
                        projectedCellIndex = cellIndex;
                    }
                }
            }

            return projectedCellIndex >= 0;
        }

        /// <summary>
        /// 计算八方向格子地图上的八角距离，作为 A* 不会高估的启发成本
        /// </summary>
        /// <param name="grid">运行时只读 Grid</param>
        /// <param name="fromCellIndex">起始 Cell 索引</param>
        /// <param name="toCellIndex">目标 Cell 索引</param>
        /// <returns>按最低地形成本换算后的启发成本</returns>
        public static bool TryCalculateLineCost(
            ref NavigationGridBlob grid,
            int fromCellIndex,
            int toCellIndex,
            float agentRadius,
            float clearanceMargin,
            float clearancePenaltyWeight,
            out float lineCost)
        {
            return TryCalculateLineCost(
                ref grid,
                fromCellIndex,
                toCellIndex,
                agentRadius,
                clearanceMargin,
                clearancePenaltyWeight,
                default,
                out lineCost);
        }

        public static bool TryCalculateLineCost(
            ref NavigationGridBlob grid,
            int fromCellIndex,
            int toCellIndex,
            float agentRadius,
            float clearanceMargin,
            float clearancePenaltyWeight,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay,
            out float lineCost)
        {
            lineCost = 0f;
            if (!NavigationGridTraversal.CanAgentOccupyDynamic(
                    ref grid,
                    fromCellIndex,
                    agentRadius,
                    clearanceMargin,
                    dynamicOverlay) ||
                !NavigationGridTraversal.CanAgentOccupyDynamic(
                    ref grid,
                    toCellIndex,
                    agentRadius,
                    clearanceMargin,
                    dynamicOverlay))
            {
                return false;
            }

            if (fromCellIndex == toCellIndex)
            {
                return true;
            }

            int currentX = fromCellIndex % grid.Width;
            int currentZ = fromCellIndex / grid.Width;
            int targetX = toCellIndex % grid.Width;
            int targetZ = toCellIndex / grid.Width;
            // 使用整数误差累计，让相同端点在不同平台上经过同一组格子
            int absoluteDeltaX = math.abs(targetX - currentX);
            int absoluteDeltaZ = math.abs(targetZ - currentZ);
            int stepX = currentX < targetX ? 1 : -1;
            int stepZ = currentZ < targetZ ? 1 : -1;
            int error = absoluteDeltaX - absoluteDeltaZ;
            float requiredClearance = NavigationGridCost.CalculateRequiredClearance(
                ref grid,
                agentRadius,
                clearanceMargin);

            // 每轮只走到正交或对角相邻格子，再检查烘焙邻接和角色所需空间
            // 这里检查的是格子层面的直达路线，不等同于物理系统的 Capsule Cast
            while (currentX != targetX || currentZ != targetZ)
            {
                int deltaX = 0;
                int deltaZ = 0;
                int doubledError = error * 2;
                if (doubledError > -absoluteDeltaZ)
                {
                    deltaX = stepX;
                    error -= absoluteDeltaZ;
                }
                if (doubledError < absoluteDeltaX)
                {
                    deltaZ = stepZ;
                    error += absoluteDeltaX;
                }

                int currentIndex = currentX + currentZ * grid.Width;
                int nextX = currentX + deltaX;
                int nextZ = currentZ + deltaZ;
                int nextIndex = nextX + nextZ * grid.Width;
                if (!NavigationGridTraversal.IsInside(nextX, nextZ, grid.Width, grid.Height) ||
                    !NavigationGridTraversal.CanAgentTraverseEdgeDynamic(
                        ref grid,
                        currentIndex,
                        nextIndex,
                        deltaX,
                        deltaZ,
                        agentRadius,
                        clearanceMargin,
                        dynamicOverlay))
                {
                    return false;
                }
                // 直线路线复用 A* 的单步成本，平滑前后才能在同一尺度下比较
                lineCost += NavigationGridCost.CalculateStepCost(
                    ref grid,
                    currentIndex,
                    nextIndex,
                    requiredClearance,
                    clearancePenaltyWeight,
                    dynamicOverlay);
                lineCost += NavigationGridCost.GetDynamicExtraCost(dynamicOverlay, nextIndex);
                currentX = nextX;
                currentZ = nextZ;
            }

            return true;
        }

        private static bool IsBetterProjectionCandidate(
            float distanceSquared,
            float terrainCost,
            float clearance,
            int cellIndex,
            float bestDistanceSquared,
            float bestTerrainCost,
            float bestClearance,
            int bestCellIndex)
        {
            // 距离相同时依次用地形成本、可用空间和格子索引决定优先级
            if (bestCellIndex < 0 || distanceSquared < bestDistanceSquared - CostEpsilon)
            {
                return true;
            }

            if (math.abs(distanceSquared - bestDistanceSquared) > CostEpsilon)
            {
                return false;
            }

            if (terrainCost < bestTerrainCost - CostEpsilon)
            {
                return true;
            }

            if (math.abs(terrainCost - bestTerrainCost) > CostEpsilon)
            {
                return false;
            }

            if (clearance > bestClearance + CostEpsilon)
            {
                return true;
            }

            return math.abs(clearance - bestClearance) <= CostEpsilon &&
                   cellIndex < bestCellIndex;
        }

        public static bool IsRequestValid(NavigationPathRequest request)
        {
            // 半径和权重必须是有限的非负值，避免破坏坐标和成本计算
            // 搜索半径设有上限，以控制扫描开销并防止整数坐标溢出
            return VectorMath.IsFinite(request.StartPosition) &&
                   VectorMath.IsFinite(request.EndPosition) &&
                   math.isfinite(request.AgentRadius) &&
                   math.isfinite(request.ClearanceMargin) &&
                   math.isfinite(request.ClearancePenaltyWeight) &&
                   math.isfinite(request.SmoothingCostTolerance) &&
                   request.AgentRadius >= 0f &&
                   request.ClearanceMargin >= 0f &&
                   request.ClearancePenaltyWeight >= 0f &&
                   request.SmoothingCostTolerance >= 0f &&
                   request.MaximumProjectionRadiusInCells >= 0;
        }

    }
}
