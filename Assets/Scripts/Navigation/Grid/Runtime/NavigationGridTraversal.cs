using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 集中判断格子和相邻边是否允许指定体型的角色通过
    /// </summary>
    public static class NavigationGridTraversal
    {
        private const float CostEpsilon = 0.00001f;

        // 静态邻接关系只读取烘焙好的 NeighborMask，运行时不临时重建地图连接
        // 基础角色半径已经包含在烘焙结果中，大体型只检查超出的空间需求
        // 对角移动还要检查两侧正交格子，防止角色从障碍物尖角挤过去
        // 动态 BlockCount 会直接阻挡格子；ExtraCost 只让路线更贵，不改变能否通行
        // 读取任何格子前都先检查导航网格尺寸和索引范围
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
        /// 在附近寻找角色可以站立的格子，并优先选择更近、更适合通行的位置
        /// </summary>
        /// <param name="grid">运行时只读 Grid</param>
        /// <param name="worldPosition">待投影世界坐标</param>
        /// <param name="agentRadius">Agent 世界半径</param>
        /// <param name="clearanceMargin">额外安全边距</param>
        /// <param name="maximumRadiusInCells">允许向外搜索的最大 Cell 半径</param>
        /// <param name="projectedCellIndex">输出投影后的 Cell 索引</param>
        /// <returns>在搜索范围内找到可站立格子时返回 true</returns>
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
            // NeighborMask 先确认静态地图允许这一步，再按当前角色体型检查空间
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

            // 大体型斜走时两侧格子也必须容得下，避免贴着狭窄墙角穿过
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
            // Blob 中记录的宽高必须与格子总数一致，格子尺寸也必须大于零
            return grid.Width > 0 &&
                   grid.Height > 0 &&
                   grid.CellSize > 0f &&
                   grid.Cells.Length == grid.Width * grid.Height;
        }

        public static bool IsInside(int x, int z, int width, int height)
        {
            // 先检查 X、Z 范围，再换算成一维索引，避免越界访问
            return x >= 0 && x < width && z >= 0 && z < height;
        }
    }
}
