using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 统一移动、地形、Clearance、Overlay 和启发式成本
    /// </summary>
    public static class NavigationGridCost
    {
        private const float MinimumTerrainCost = 0.01f;
        private const float SquareRootTwo = 1.41421356237f;

        // Terrain Cost 下限同时用于真实步进和启发函数，保持启发式可采纳
        // 对角距离常量必须与八方向邻接的几何长度完全一致
        // Clearance 惩罚始终非负，因此不会破坏 A 星最短路径条件
        // 动态额外成本只在目标 Cell 计入，避免同一步重复收费
        // 本类不判断边是否合法，通行性统一由 Traversal 提供
        public static float CalculateRequiredClearance(
            ref NavigationGridBlob grid,
            float agentRadius,
            float clearanceMargin)
        {
            // 烘焙已包含 BaseAgentRadius，运行时只计算更大体型和安全边距的增量
            return math.max(0f, agentRadius - grid.BaseAgentRadius) +
                   math.max(0f, clearanceMargin);
        }

        public static float CalculateStepCost(
            ref NavigationGridBlob grid,
            int fromCellIndex,
            int toCellIndex,
            float requiredClearance,
            float clearancePenaltyWeight)
        {
            return CalculateStepCost(
                ref grid,
                fromCellIndex,
                toCellIndex,
                requiredClearance,
                clearancePenaltyWeight,
                default);
        }

        public static float CalculateStepCost(
            ref NavigationGridBlob grid,
            int fromCellIndex,
            int toCellIndex,
            float requiredClearance,
            float clearancePenaltyWeight,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay)
        {
            // 边成本由几何距离、地形权重和低 Clearance 惩罚组成
            // 所有项保持非负是启发函数可采纳和 G Cost 单调增长的前提
            int fromX = fromCellIndex % grid.Width;
            int fromZ = fromCellIndex / grid.Width;
            int toX = toCellIndex % grid.Width;
            int toZ = toCellIndex / grid.Width;
            bool diagonal = fromX != toX && fromZ != toZ;
            float distance = grid.CellSize * (diagonal ? SquareRootTwo : 1f);
            // 使用目标 Cell 成本使每条有向边只采样一次，并与 A 星和直线检查保持一致
            NavigationGridCell targetCell = grid.Cells[toCellIndex];
            float reduction = dynamicOverlay.IsCreated &&
                              toCellIndex >= 0 &&
                              toCellIndex < dynamicOverlay.Length
                ? math.max(0f, dynamicOverlay[toCellIndex].ClearanceReduction)
                : 0f;
            float effectiveClearance = math.max(0f, targetCell.Clearance - reduction);
            float extraClearance = math.max(0f, effectiveClearance - requiredClearance);
            // 通道越宽比例越接近零，惩罚连续衰减而不会形成新的硬阻挡
            float clearanceRatio = grid.CellSize / (grid.CellSize + extraClearance);
            float weightedTerrainCost =
                math.max(MinimumTerrainCost, targetCell.TerrainCost) +
                math.max(0f, clearancePenaltyWeight) * clearanceRatio;
            return distance * weightedTerrainCost;
        }

        public static float GetDynamicExtraCost(
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay,
            int cellIndex)
        {
            return dynamicOverlay.IsCreated &&
                   cellIndex >= 0 &&
                   cellIndex < dynamicOverlay.Length
                ? math.max(0f, dynamicOverlay[cellIndex].ExtraCost)
                : 0f;
        }

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
            // Terrain Cost 下限同为 MinimumTerrainCost，因此该估价不会高估真实成本
            // Clearance 惩罚始终非负，不加入启发函数仍保持可采纳性
            return grid.CellSize * MinimumTerrainCost *
                   (diagonalSteps * SquareRootTwo + straightSteps);
        }

    }
}
