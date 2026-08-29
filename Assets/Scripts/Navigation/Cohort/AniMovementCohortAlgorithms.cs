using AnimarsCatcher.Core;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 提供 Cohort 切分、目标格容量和自由移动所需的确定性计算
    /// </summary>
    public static class AniMovementCohortAlgorithms
    {
        public const int DefaultMemberCapacity = 64;
        public const int HardMemberCapacity = 128;

        /// <summary>
        /// 把二维 Cell 坐标编码成保持空间邻近性的 Morton Key
        /// </summary>
        public static ulong CalculateMortonKey(int2 coordinate)
        {
            uint x = unchecked((uint)math.max(0, coordinate.x));
            uint z = unchecked((uint)math.max(0, coordinate.y));
            return InterleaveBits(x) | (InterleaveBits(z) << 1);
        }

        /// <summary>
        /// 根据首选值和硬上限取得本轮真正使用的 Cohort 容量
        /// </summary>
        public static int ResolveMemberCapacity(AniMovementCohortSettings settings)
        {
            int hardCapacity = settings.MaximumMemberCapacity <= 0
                ? HardMemberCapacity
                : math.clamp(settings.MaximumMemberCapacity, 1, HardMemberCapacity);
            int preferredCapacity = settings.PreferredMemberCapacity <= 0
                ? DefaultMemberCapacity
                : settings.PreferredMemberCapacity;
            return math.clamp(preferredCapacity, 1, hardCapacity);
        }

        /// <summary>
        /// 计算一个 Grid Cell 在当前 Ani 半径下可提供的自然落点数量
        /// </summary>
        public static int CalculateCellCapacity(
            float cellSize,
            float agentRadius,
            float capacityScale,
            out int slotsPerAxis)
        {
            float diameter = math.max(0.02f, agentRadius * 2f);
            slotsPerAxis = math.max(1, (int)math.floor(math.max(0.01f, cellSize) / diameter));
            int physicalCapacity = math.max(1, slotsPerAxis * slotsPerAxis);
            float safeScale = math.isfinite(capacityScale)
                ? math.max(0.01f, capacityScale)
                : 1f;
            return math.clamp(
                (int)math.floor(physicalCapacity * safeScale),
                1,
                physicalCapacity);
        }

        /// <summary>
        /// 在 Cell 内为容量槽生成居中的世界坐标
        /// </summary>
        public static float3 CalculateGoalPosition(
            float3 cellCenter,
            float cellSize,
            int slotIndex,
            int slotsPerAxis)
        {
            int safeAxis = math.max(1, slotsPerAxis);
            int x = math.clamp(slotIndex % safeAxis, 0, safeAxis - 1);
            int z = math.clamp(slotIndex / safeAxis, 0, safeAxis - 1);
            float spacing = cellSize / safeAxis;
            float firstOffset = -cellSize * 0.5f + spacing * 0.5f;
            return cellCenter + new float3(
                firstOffset + x * spacing,
                0f,
                firstOffset + z * spacing);
        }

        /// <summary>
        /// 按剩余距离计算带制动的目标速度
        /// </summary>
        public static float3 CalculateArrivalVelocity(
            float3 currentPosition,
            float3 targetPosition,
            float maximumSpeed,
            float maximumAcceleration,
            float arrivalRadius)
        {
            float3 offset = PlanarMath.FlattenY(targetPosition - currentPosition);
            float distance = math.length(offset);
            float stopDistance = math.max(0f, arrivalRadius);
            if (distance <= stopDistance)
            {
                return float3.zero;
            }

            float speedLimit = math.max(0f, maximumSpeed);
            float acceleration = math.max(0.01f, maximumAcceleration);
            // 用 v²=2as 限制接近目标时的速度，确保剩余距离足以完成制动
            float brakingDistance = speedLimit * speedLimit / (2f * acceleration);
            float speed = distance <= stopDistance + brakingDistance
                ? math.sqrt(math.max(0f, 2f * acceleration * (distance - stopDistance)))
                : speedLimit;
            return PlanarMath.NormalizeXZOrDefault(offset, float3.zero) *
                   math.min(speedLimit, speed);
        }

        /// <summary>
        /// 将共享流向场方向与靠近目标后的个人落点方向平滑混合
        /// </summary>
        public static float3 BlendGoalVelocity(
            float3 flowDirection,
            float3 arrivalVelocity,
            float distanceToGoal,
            float influenceRadius,
            bool canApproachGoalDirectly)
        {
            float speed = math.length(arrivalVelocity);
            if (speed <= 1e-5f)
            {
                return float3.zero;
            }

            float3 directDirection = arrivalVelocity / speed;
            float3 safeFlow = math.normalizesafe(PlanarMath.FlattenY(flowDirection));
            if (!canApproachGoalDirectly)
            {
                return safeFlow * speed;
            }

            // 直达成立后立即退出中心 Flow，避免两个相反方向形成平衡点或循环路线
            return directDirection * speed;
        }

        /// <summary>
        /// 从导航分组的稀疏流向场中读取当前 Cell 的下一步方向
        /// </summary>
        public static bool TryGetFlowDirection(
            DynamicBuffer<NavigationFlowFieldCell> field,
            int cellIndex,
            out float3 direction)
        {
            direction = float3.zero;
            int minimum = 0;
            int maximum = field.Length - 1;
            // 构建器保证稀疏流向场按 CellIndex 排序，读取成本不再随走廊长度线性增长
            while (minimum <= maximum)
            {
                int index = minimum + ((maximum - minimum) >> 1);
                NavigationFlowFieldCell cell = field[index];
                if (cell.CellIndex == cellIndex)
                {
                    direction = math.normalizesafe(
                        new float3(cell.Direction.x, 0f, cell.Direction.y));
                    return true;
                }

                if (cell.CellIndex < cellIndex)
                {
                    minimum = index + 1;
                }
                else
                {
                    maximum = index - 1;
                }
            }

            return false;
        }

        private static ulong InterleaveBits(uint value)
        {
            ulong bits = value;
            bits = (bits | bits << 16) & 0x0000FFFF0000FFFFUL;
            bits = (bits | bits << 8) & 0x00FF00FF00FF00FFUL;
            bits = (bits | bits << 4) & 0x0F0F0F0F0F0F0F0FUL;
            bits = (bits | bits << 2) & 0x3333333333333333UL;
            bits = (bits | bits << 1) & 0x5555555555555555UL;
            return bits;
        }
    }
}
