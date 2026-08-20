using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 统一静态拓扑、Clearance 和动态 Overlay 通行规则
    /// </summary>
    public static class NavigationGridTraversal
    {
        private const float CostEpsilon = 0.00001f;

        // NeighborMask 是静态边合法性的唯一来源，运行时不能重建拓扑
        // 大体型占用在烘焙基础半径之上只计算额外 Clearance
        // 对角移动还要验证两个正交侧边，防止从障碍角点挤过
        // 动态 BlockCount 覆盖静态可行走结果，ExtraCost 不改变可行走性
        // 所有索引访问前必须先通过统一形状和边界检查
        public static bool IsDynamicCellBlocked(
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay,
            int cellIndex)
        {
            return dynamicOverlay.IsCreated &&
                   cellIndex >= 0 &&
                   cellIndex < dynamicOverlay.Length &&
                   dynamicOverlay[cellIndex].BlockCount > 0;
        }

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
            float requiredClearance = NavigationGridCost.CalculateRequiredClearance(
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
        public static bool CanAgentOccupyDynamic(
            ref NavigationGridBlob grid,
            int cellIndex,
            float agentRadius,
            float clearanceMargin,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay)
        {
            if (!CanAgentOccupy(ref grid, cellIndex, agentRadius, clearanceMargin) ||
                IsDynamicCellBlocked(dynamicOverlay, cellIndex))
            {
                return false;
            }

            float reduction = dynamicOverlay.IsCreated &&
                              cellIndex >= 0 &&
                              cellIndex < dynamicOverlay.Length
                ? math.max(0f, dynamicOverlay[cellIndex].ClearanceReduction)
                : 0f;
            float requiredClearance = NavigationGridCost.CalculateRequiredClearance(
                ref grid,
                agentRadius,
                clearanceMargin);
            return math.max(0f, grid.Cells[cellIndex].Clearance - reduction) >=
                   requiredClearance;
        }

        public static bool CanAgentTraverseEdgeDynamic(
            ref NavigationGridBlob grid,
            int fromCellIndex,
            int toCellIndex,
            int deltaX,
            int deltaZ,
            float agentRadius,
            float clearanceMargin,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay)
        {
            if (!CanAgentTraverseEdge(
                    ref grid,
                    fromCellIndex,
                    toCellIndex,
                    deltaX,
                    deltaZ,
                    agentRadius,
                    clearanceMargin) ||
                !CanAgentOccupyDynamic(
                    ref grid,
                    fromCellIndex,
                    agentRadius,
                    clearanceMargin,
                    dynamicOverlay) ||
                !CanAgentOccupyDynamic(
                    ref grid,
                    toCellIndex,
                    agentRadius,
                    clearanceMargin,
                    dynamicOverlay))
            {
                return false;
            }

            if (deltaX == 0 || deltaZ == 0)
            {
                return true;
            }

            int fromX = fromCellIndex % grid.Width;
            int fromZ = fromCellIndex / grid.Width;
            int sideXCellIndex = fromX + deltaX + fromZ * grid.Width;
            int sideZCellIndex = fromX + (fromZ + deltaZ) * grid.Width;
            return CanAgentOccupyDynamic(
                       ref grid,
                       sideXCellIndex,
                       agentRadius,
                       clearanceMargin,
                       dynamicOverlay) &&
                   CanAgentOccupyDynamic(
                       ref grid,
                       sideZCellIndex,
                       agentRadius,
                       clearanceMargin,
                       dynamicOverlay);
        }

        public static bool CanAgentTraverseEdge(
            ref NavigationGridBlob grid,
            int fromCellIndex,
            int toCellIndex,
            int deltaX,
            int deltaZ,
            float agentRadius,
            float clearanceMargin)
        {
            // NeighborMask 约束静态几何，CanAgentOccupy 再应用当前体型
            if (!NavigationGridDirections.TryGetDirectionIndex(deltaX, deltaZ, out int directionIndex) ||
                (grid.Cells[fromCellIndex].NeighborMask & (1 << directionIndex)) == 0 ||
                !CanAgentOccupy(ref grid, toCellIndex, agentRadius, clearanceMargin))
            {
                return false;
            }

            if (deltaX == 0 || deltaZ == 0)
            {
                return true;
            }

            // 大体型对角移动还要占用两个正交侧边，防止从低 Clearance 角点挤过
            int fromX = fromCellIndex % grid.Width;
            int fromZ = fromCellIndex / grid.Width;
            int sideXCellIndex = fromX + deltaX + fromZ * grid.Width;
            int sideZCellIndex = fromX + (fromZ + deltaZ) * grid.Width;
            return CanAgentOccupy(
                       ref grid,
                       sideXCellIndex,
                       agentRadius,
                       clearanceMargin) &&
                   CanAgentOccupy(
                       ref grid,
                       sideZCellIndex,
                       agentRadius,
                       clearanceMargin);
        }

        public static bool IsGridShapeValid(ref NavigationGridBlob grid)
        {
            // Blob 尺寸和 Cell 数必须完全一致
            // 非正 CellSize 会破坏距离成本和坐标转换
            return grid.Width > 0 &&
                   grid.Height > 0 &&
                   grid.CellSize > 0f &&
                   grid.Cells.Length == grid.Width * grid.Height;
        }

        public static bool IsInside(int x, int z, int width, int height)
        {
            // 坐标边界在转换为行主序索引前统一验证
            return x >= 0 && x < width && z >= 0 && z < height;
        }
    }
}
