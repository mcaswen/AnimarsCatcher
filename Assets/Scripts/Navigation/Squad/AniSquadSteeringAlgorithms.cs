using AnimarsCatcher.Core;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 提供确定性的 Squad Flow 采样、Anchor 移动和槽位跟随计算
    /// </summary>
    public static class AniSquadSteeringAlgorithms
    {
        // Steering 只消费 Formation 结果，不重新计算列数和职责槽位
        // Flow Buffer 按 CellIndex 稀疏存储，采样必须保持稳定索引语义
        // Anchor 制动和成员跟随都限制在 XZ 平面
        // 本类只计算目标速度，唯一 Transform 写回仍属于移动提交系统
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
        /// <param name="stoppingDistance">指令到达半径</param>
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

    }
}
