using Unity.Mathematics;

namespace AnimarsCatcher.Animars.Movement.Grid
{
    public static partial class NavigationGridPathAlgorithms
    {
        // 本文件只承载坐标查询 端点投影和离散直线检查
        // A 星搜索与 Open Set 分离到同一 partial 类型的其他文件

        /// <summary>
        /// 把 Grid 范围内的世界坐标转换为稳定 Cell 坐标和索引
        /// </summary>
        /// <param name="grid">运行时只读 Grid</param>
        /// <param name="worldPosition">待转换世界坐标</param>
        /// <param name="coordinate">输出 Cell 二维坐标</param>
        /// <param name="cellIndex">输出行主序 Cell 索引</param>
        /// <returns>世界坐标位于有效 XZ Bounds 内时返回 true</returns>
        public static bool TryWorldToCell(
            ref NavigationGridBlob grid,
            float3 worldPosition,
            out int2 coordinate,
            out int cellIndex)
        {
            coordinate = default;
            cellIndex = -1;
            if (!IsGridShapeValid(ref grid) || !math.all(math.isfinite(worldPosition)))
            {
                return false;
            }

            float2 localPosition = new float2(
                worldPosition.x - grid.BoundsMinimum.x,
                worldPosition.z - grid.BoundsMinimum.z);
            // Bounds 使用左闭右开区间 防止最大边界被映射为 Width 或 Height
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
        /// 把稳定 Cell 索引转换为烘焙表面上的世界坐标
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
        /// 判断指定 Agent 是否能占用目标 Cell
        /// </summary>
        /// <param name="grid">运行时只读 Grid</param>
        /// <param name="cellIndex">待检查 Cell 索引</param>
        /// <param name="agentRadius">Agent 世界半径</param>
        /// <param name="clearanceMargin">额外安全边距</param>
        /// <returns>基础可行走且剩余 Clearance 足够时返回 true</returns>
        public static bool CanAgentOccupy(
            ref NavigationGridBlob grid,
            int cellIndex,
            float agentRadius,
            float clearanceMargin)
        {
            if (cellIndex < 0 || cellIndex >= grid.Cells.Length)
            {
                return false;
            }

            NavigationGridCell cell = grid.Cells[cellIndex];
            float requiredClearance = CalculateRequiredClearance(
                ref grid,
                agentRadius,
                clearanceMargin);
            return cell.Walkable != 0 && cell.Clearance + CostEpsilon >= requiredClearance;
        }

        /// <summary>
        /// 按距离、地形成本、Clearance 和 Cell Index 稳定投影到邻近合法 Cell
        /// </summary>
        /// <param name="grid">运行时只读 Grid</param>
        /// <param name="worldPosition">待投影世界坐标</param>
        /// <param name="agentRadius">Agent 世界半径</param>
        /// <param name="clearanceMargin">额外安全边距</param>
        /// <param name="maximumRadiusInCells">允许向外搜索的最大 Cell 半径</param>
        /// <param name="projectedCellIndex">输出投影后的 Cell 索引</param>
        /// <returns>搜索范围内存在合法 Cell 时返回 true</returns>
        public static bool TryProjectToNearestCell(
            ref NavigationGridBlob grid,
            float3 worldPosition,
            float agentRadius,
            float clearanceMargin,
            int maximumRadiusInCells,
            out int projectedCellIndex)
        {
            projectedCellIndex = -1;
            if (!IsGridShapeValid(ref grid) ||
                !math.all(math.isfinite(worldPosition)) ||
                !math.isfinite(agentRadius) ||
                !math.isfinite(clearanceMargin) ||
                agentRadius < 0f ||
                clearanceMargin < 0f ||
                maximumRadiusInCells < 0)
            {
                return false;
            }

            // raw 坐标故意不先 Clamp 使 Grid 外位置仍受最大投影半径约束
            int rawX = (int)math.floor(
                (worldPosition.x - grid.BoundsMinimum.x) / grid.CellSize);
            int rawZ = (int)math.floor(
                (worldPosition.z - grid.BoundsMinimum.z) / grid.CellSize);
            int minimumX = math.max(0, rawX - maximumRadiusInCells);
            int maximumX = math.min(grid.Width - 1, rawX + maximumRadiusInCells);
            int minimumZ = math.max(0, rawZ - maximumRadiusInCells);
            int maximumZ = math.min(grid.Height - 1, rawZ + maximumRadiusInCells);

            // 候选使用字典序比较 不依赖循环提前退出或容器遍历顺序
            // 距离相同优先低 Terrain Cost
            // 地形成本相同优先高 Clearance
            // 所有连续值都相同时优先更小 Cell Index
            // 这套规则同时覆盖 Grid 内阻挡端点和 Grid 外近边界端点
            float bestDistanceSquared = float.PositiveInfinity;
            float bestTerrainCost = float.PositiveInfinity;
            float bestClearance = float.NegativeInfinity;

            // 扫描完整候选方形后统一比较 避免只取首个搜索环导致角点候选错误胜出
            // 第一关键字是到原世界坐标的平方距离 第二关键字才是地形和 Clearance
            for (int z = minimumZ; z <= maximumZ; z++)
            {
                for (int x = minimumX; x <= maximumX; x++)
                {
                    int cellIndex = x + z * grid.Width;
                    if (!CanAgentOccupy(
                            ref grid,
                            cellIndex,
                            agentRadius,
                            clearanceMargin))
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
                    if (IsBetterProjectionCandidate(
                            distanceSquared,
                            terrainCost,
                            cell.Clearance,
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
        /// 计算八方向 Grid 上保持可采纳性的 Octile Distance
        /// </summary>
        /// <param name="grid">运行时只读 Grid</param>
        /// <param name="fromCellIndex">起始 Cell 索引</param>
        /// <param name="toCellIndex">目标 Cell 索引</param>
        /// <returns>使用最低地形成本缩放后的启发成本</returns>
        public static float CalculateOctileHeuristic(
            ref NavigationGridBlob grid,
            int fromCellIndex,
            int toCellIndex)
        {
            int fromX = fromCellIndex % grid.Width;
            int fromZ = fromCellIndex / grid.Width;
            int toX = toCellIndex % grid.Width;
            int toZ = toCellIndex / grid.Width;
            int deltaX = math.abs(toX - fromX);
            int deltaZ = math.abs(toZ - fromZ);
            int diagonalSteps = math.min(deltaX, deltaZ);
            int straightSteps = math.max(deltaX, deltaZ) - diagonalSteps;
            // Terrain Cost 运行时下限也是 MinimumTerrainCost 因此该估价不会高估真实成本
            // Clearance 惩罚始终非负 不加入启发函数仍保持可采纳性
            return grid.CellSize * MinimumTerrainCost *
                   (diagonalSteps * SquareRootTwo + straightSteps);
        }

        /// <summary>
        /// 沿 Cell 中心连线验证邻接和占用条件并计算直接移动成本
        /// </summary>
        /// <param name="grid">运行时只读 Grid</param>
        /// <param name="fromCellIndex">线段起始 Cell</param>
        /// <param name="toCellIndex">线段目标 Cell</param>
        /// <param name="agentRadius">Agent 世界半径</param>
        /// <param name="clearanceMargin">额外安全边距</param>
        /// <param name="clearancePenaltyWeight">低 Clearance 惩罚权重</param>
        /// <param name="lineCost">输出直线路径成本</param>
        /// <returns>线段经过的全部离散边都合法时返回 true</returns>
        public static bool TryCalculateLineCost(
            ref NavigationGridBlob grid,
            int fromCellIndex,
            int toCellIndex,
            float agentRadius,
            float clearanceMargin,
            float clearancePenaltyWeight,
            out float lineCost)
        {
            lineCost = 0f;
            if (!CanAgentOccupy(ref grid, fromCellIndex, agentRadius, clearanceMargin) ||
                !CanAgentOccupy(ref grid, toCellIndex, agentRadius, clearanceMargin))
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
            // 误差累加器在整数域工作 相同端点不会受浮点舍入和平台差异影响
            // 每次迭代最多同时推进 X 和 Z
            // 同时推进表示一次合法对角边
            // 单轴推进表示一次正交边
            // 因此生成的离散序列始终能由 NeighborMask 验证
            int absoluteDeltaX = math.abs(targetX - currentX);
            int absoluteDeltaZ = math.abs(targetZ - currentZ);
            int stepX = currentX < targetX ? 1 : -1;
            int stepZ = currentZ < targetZ ? 1 : -1;
            int error = absoluteDeltaX - absoluteDeltaZ;
            float requiredClearance = CalculateRequiredClearance(
                ref grid,
                agentRadius,
                clearanceMargin);

            // Bresenham 每轮只产生一个相邻 Cell 对角步仍由烘焙邻接和体型 Clearance 共同约束
            // 这里验证的是平滑后可直接连接的离散通道 不是物理层最终 Capsule Cast
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
                if (!IsInside(nextX, nextZ, grid.Width, grid.Height) ||
                    !CanAgentTraverseEdge(
                        ref grid,
                        currentIndex,
                        nextX + nextZ * grid.Width,
                        deltaX,
                        deltaZ,
                        agentRadius,
                        clearanceMargin))
                {
                    return false;
                }

                int nextIndex = nextX + nextZ * grid.Width;
                // 直线成本复用 A 星步进成本 保证平滑前后能够按同一尺度比较
                lineCost += CalculateStepCost(
                    ref grid,
                    currentIndex,
                    nextIndex,
                    requiredClearance,
                    clearancePenaltyWeight);
                currentX = nextX;
                currentZ = nextZ;
            }

            return true;
        }

    }
}
