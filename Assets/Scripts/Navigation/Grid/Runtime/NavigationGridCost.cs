using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 集中计算路径搜索用到的步进、地形、空间余量和动态障碍成本
    /// </summary>
    public static class NavigationGridCost
    {
        private const float MinimumTerrainCost = 0.01f;
        private const float SquareRootTwo = 1.41421356237f;

        // 地形成本下限同时用于实际步进和启发函数，保证启发值不会高估真实成本
        // 对角步长必须与八方向格子的实际几何距离一致
        // 空间不足的惩罚始终为非负值，不会破坏 A* 的最短路径条件
        // 一步移动只计算目标格子的动态附加成本，避免重复计费
        // 本类只负责算成本；能否通行统一交给 NavigationGridTraversal 判断
        public static float CalculateRequiredClearance(
            ref NavigationGridBlob grid,
            float agentRadius,
            float clearanceMargin)
        {
            // 烘焙已经为基础角色半径预留空间，运行时只补上更大体型和额外安全距离
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
            // 每一步的成本由移动距离、地形权重和狭窄空间惩罚组成
            // 所有项目都必须非负，才能保证累计成本只增不减且启发函数有效
            int fromX = fromCellIndex % grid.Width;
            int fromZ = fromCellIndex / grid.Width;
            int toX = toCellIndex % grid.Width;
            int toZ = toCellIndex / grid.Width;
            bool diagonal = fromX != toX && fromZ != toZ;
            float distance = grid.CellSize * (diagonal ? SquareRootTwo : 1f);
            // 使用目标格子的地形和动态成本，让 A* 与直线检查采用同一计算规则
            NavigationGridCell targetCell = grid.Cells[toCellIndex];
            float reduction = dynamicOverlay.IsCreated &&
                              toCellIndex >= 0 &&
                              toCellIndex < dynamicOverlay.Length
                ? math.max(0f, dynamicOverlay[toCellIndex].ClearanceReduction)
                : 0f;
            float effectiveClearance = math.max(0f, targetCell.Clearance - reduction);
            float extraClearance = math.max(0f, effectiveClearance - requiredClearance);
            // 通道越宽，惩罚越接近零；它只影响路线偏好，不会额外制造不可通行区域
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
            // 启发函数使用与实际步进相同的最低地形成本，因此不会高估剩余路程
            // 狭窄空间惩罚不会为负，省略它仍可保持启发函数安全有效
            return grid.CellSize * MinimumTerrainCost *
                   (diagonalSteps * SquareRootTwo + straightSteps);
        }

    }
}
