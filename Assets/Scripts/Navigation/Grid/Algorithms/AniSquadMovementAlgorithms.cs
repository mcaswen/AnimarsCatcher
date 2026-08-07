using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 提供阶段四阵型、速度和局部 Field 采样的无状态计算
    /// </summary>
    public static class AniSquadMovementAlgorithms
    {
        /// <summary>
        /// 计算给定成员数量下的固定阵型列数
        /// </summary>
        /// <param name="kind">阵型类型</param>
        /// <param name="memberCount">当前成员数量</param>
        /// <param name="configuredColumns">紧凑矩形的配置列数</param>
        /// <returns>至少为一且不超过成员数量的列数</returns>
        public static int CalculateColumnCount(
            AniSquadFormationKind kind,
            int memberCount,
            int configuredColumns)
        {
            int count = math.max(1, memberCount);
            if (kind == AniSquadFormationKind.Column)
            {
                // 纵队是单列特例，忽略外部列数避免布局形状漂移
                return 1;
            }

            // 紧凑矩形限制在成员数量内，防止产生没有成员的尾列
            return math.clamp(configuredColumns, 1, count);
        }

        /// <summary>
        /// 计算以全部成员中心为原点的稳定槽位偏移
        /// </summary>
        /// <param name="slotIndex">槽位稳定索引</param>
        /// <param name="memberCount">当前成员数量</param>
        /// <param name="kind">阵型类型</param>
        /// <param name="configuredColumns">矩形阵型的最大列数</param>
        /// <param name="horizontalSpacing">同一行的水平间距</param>
        /// <param name="longitudinalSpacing">相邻行的纵向间距</param>
        /// <returns>以阵型中心为原点的局部空间偏移</returns>
        public static float3 CalculateSlotOffset(
            int slotIndex,
            int memberCount,
            AniSquadFormationKind kind,
            int configuredColumns,
            float horizontalSpacing,
            float longitudinalSpacing)
        {
            int count = math.max(1, memberCount);
            int columns = CalculateColumnCount(kind, count, configuredColumns);
            int row = math.clamp(slotIndex / columns, 0, (count - 1) / columns);
            int rowStart = row * columns;
            int rowCount = math.min(columns, count - rowStart);
            int column = math.clamp(slotIndex - rowStart, 0, rowCount - 1);

            // 每行按实际成员数重新居中，奇数尾行仍保持横向对称
            float x = (column - (rowCount - 1) * 0.5f) * horizontalSpacing;
            float meanRowOffset = CalculateMeanRowOffset(
                count,
                columns,
                longitudinalSpacing);
            float z = -row * longitudinalSpacing - meanRowOffset;
            if (kind == AniSquadFormationKind.Column)
            {
                // 纵队沿纵向展开但保持横向中心线不变量
                x = 0f;
            }

            return new float3(x, 0f, z);
        }

        /// <summary>
        /// 从稀疏 Field Buffer 按稳定 Cell 索引取得当前下降方向
        /// </summary>
        /// <param name="field">单个 Squad 的局部 Field</param>
        /// <param name="cellIndex">当前所在 Cell</param>
        /// <param name="direction">输出 XZ 平面方向</param>
        /// <returns>Field 中存在该 Cell 且方向有效时返回 true</returns>
        public static bool TryGetFlowDirection(
            DynamicBuffer<NavigationFlowFieldCell> field,
            int cellIndex,
            out float3 direction)
        {
            direction = float3.zero;

            // Field Buffer 按 CellIndex 稀疏存储，线性扫描避免为单次成员采样建临时字典
            for (int index = 0; index < field.Length; index++)
            {
                NavigationFlowFieldCell cell = field[index];
                if (cell.CellIndex != cellIndex)
                {
                    continue;
                }

                direction = math.normalizesafe(
                    new float3(cell.Direction.x, 0f, cell.Direction.y));

                // 零方向被视为有效 Cell 但不会驱动 Anchor
                return true;
            }

            return false;
        }

        /// <summary>
        /// 按槽位误差、锚点前馈速度和成员速度上限计算成员期望速度
        /// </summary>
        /// <param name="currentPosition">成员当前位置</param>
        /// <param name="slotTarget">成员槽位目标</param>
        /// <param name="anchorVelocity">Squad Anchor 当前速度</param>
        /// <param name="maximumSpeed">成员最大速度</param>
        /// <returns>未经过加速度限制的目标速度</returns>
        public static float3 CalculateSlotVelocity(
            float3 currentPosition,
            float3 slotTarget,
            float3 anchorVelocity,
            float maximumSpeed)
        {
            float3 error = slotTarget - currentPosition;
            error = PlanarMath.FlattenY(error);
            float distance = math.length(error);
            float3 desired = anchorVelocity + error * 2.5f;
            float speed = math.length(desired);
            if (speed <= maximumSpeed || speed <= 1e-5f)
            {
                // 低于上限时保留 Anchor 前馈和槽位纠偏的合成速度
                return desired;
            }

            return desired * (maximumSpeed / speed);
        }

        /// <summary>
        /// 按到达距离和制动距离计算 Anchor 目标速度
        /// </summary>
        /// <param name="currentPosition">Anchor 当前位置</param>
        /// <param name="targetPosition">解析后的目标位置</param>
        /// <param name="maximumSpeed">成员聚合后的最大 Anchor 速度</param>
        /// <param name="maximumAcceleration">成员聚合后的最大 Anchor 加速度</param>
        /// <param name="stoppingDistance">订单到达半径</param>
        /// <returns>未经过加速度限制的 Anchor 目标速度</returns>
        public static float3 CalculateAnchorVelocity(
            float3 currentPosition,
            float3 targetPosition,
            float maximumSpeed,
            float maximumAcceleration,
            float stoppingDistance)
        {
            float3 offset = targetPosition - currentPosition;
            offset = PlanarMath.FlattenY(offset);
            float distance = math.length(offset);
            if (distance <= math.max(0f, stoppingDistance))
            {
                // 进入停止半径后返回零速度，Progress 会继续等待成员槽位稳定
                return float3.zero;
            }

            // 以 v²/(2a) 估算制动距离，在接近目标时平滑降低 Anchor 速度
            float brakingDistance = maximumSpeed * maximumSpeed /
                                     math.max(2f * maximumAcceleration, 1e-3f);
            float speed = distance <= stoppingDistance + brakingDistance
                ? math.sqrt(math.max(0f, 2f * maximumAcceleration *
                                      (distance - stoppingDistance)))
                : maximumSpeed;
            return PlanarMath.NormalizeXZOrDefault(offset, float3.zero) *
                   math.min(maximumSpeed, speed);
        }

        /// <summary>
        /// 把阵型局部偏移转换为世界空间槽位位置
        /// </summary>
        /// <param name="anchorPosition">Anchor 世界位置</param>
        /// <param name="anchorRotation">Anchor 水平旋转</param>
        /// <param name="localOffset">槽位局部偏移</param>
        /// <returns>槽位世界位置</returns>
        public static float3 CalculateSlotWorldPosition(
            float3 anchorPosition,
            quaternion anchorRotation,
            float3 localOffset)
        {
            // Anchor 旋转只负责水平阵型朝向，局部槽位通过同一旋转变换到世界空间
            return anchorPosition + math.mul(anchorRotation, localOffset);
        }

        private static float CalculateMeanRowOffset(
            int memberCount,
            int columns,
            float longitudinalSpacing)
        {
            int rowCount = (memberCount + columns - 1) / columns;
            float sum = 0f;

            // 以所有行的实际成员数加权求平均，保证不完整尾行也参与中心校正
            for (int row = 0; row < rowCount; row++)
            {
                int countInRow = math.min(columns, memberCount - row * columns);
                sum += countInRow * (-row * longitudinalSpacing);
            }

            return sum / math.max(1, memberCount);
        }
    }
}
