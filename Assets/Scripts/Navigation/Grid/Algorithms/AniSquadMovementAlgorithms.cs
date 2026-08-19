using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
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
        /// 根据前视可用宽度计算阶段五的目标列数
        /// </summary>
        /// <param name="kind">当前阵型类型</param>
        /// <param name="memberCount">当前有效成员数量</param>
        /// <param name="usableWidth">扣除边界余量后的前视宽度</param>
        /// <param name="maximumAgentDiameter">队伍最大成员直径</param>
        /// <param name="horizontalGap">相邻列之间的额外间距</param>
        /// <returns>限制在一到成员数量之间的目标列数</returns>
        public static int CalculateAdaptiveColumnCount(
            AniSquadFormationKind kind,
            int memberCount,
            float usableWidth,
            float maximumAgentDiameter,
            float horizontalGap)
        {
            int count = math.max(1, memberCount);
            if (kind == AniSquadFormationKind.Column)
            {
                return 1;
            }

            float columnWidth = math.max(0.01f, maximumAgentDiameter) +
                                math.max(0f, horizontalGap);
            int columns = (int)math.floor(
                (math.max(0f, usableWidth) + math.max(0f, horizontalGap)) /
                columnWidth);
            return math.clamp(columns, 1, count);
        }

        /// <summary>
        /// 为前排和后排生成稳定的职责槽位偏好
        /// </summary>
        /// <param name="slotIndex">稳定槽位索引</param>
        /// <param name="memberCount">当前有效成员数量</param>
        /// <param name="columnCount">当前阵型列数</param>
        /// <returns>前排 Picker、后排 Blaster、中间排 Any</returns>
        public static AniSquadRole CalculateSlotRole(
            int slotIndex,
            int memberCount,
            int columnCount)
        {
            int columns = math.max(1, columnCount);
            int count = math.max(1, memberCount);
            int row = math.max(0, slotIndex) / columns;
            int lastRow = (count - 1) / columns;
            if (row == 0)
            {
                return AniSquadRole.Picker;
            }

            return row == lastRow ? AniSquadRole.Blaster : AniSquadRole.Any;
        }

        /// <summary>
        /// 使用确定性的 Hungarian 匹配求解成员到槽位的最小总代价
        /// </summary>
        /// <param name="costMatrix">按成员行、槽位列连续存储的非负有限代价</param>
        /// <param name="memberCount">需要分配的成员数量</param>
        /// <param name="slotCount">可用槽位数量，必须不少于成员数量</param>
        /// <param name="assignments">逐成员输出槽位索引</param>
        /// <returns>输入有效并生成完整一对一匹配时返回 true</returns>
        public static bool TrySolveMinimumCostAssignment(
            NativeArray<float> costMatrix,
            int memberCount,
            int slotCount,
            NativeArray<int> assignments)
        {
            // 当前阵型是一名成员对应一个槽位，因此只接受成员数不大于槽位数的矩阵
            // 输入容器由调用方持有，求解器只写 assignments，不修改原始代价
            // 维度检查在任何临时分配前完成，异常调用不会产生额外运行时负担
            if (!costMatrix.IsCreated ||
                !assignments.IsCreated ||
                memberCount <= 0 ||
                slotCount < memberCount ||
                costMatrix.Length < memberCount * slotCount ||
                assignments.Length < memberCount)
            {
                return false;
            }

            NativeArray<float> memberPotentials = new(
                memberCount + 1,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory);
            NativeArray<float> slotPotentials = new(
                slotCount + 1,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory);
            NativeArray<float> minimumCosts = new(
                slotCount + 1,
                Allocator.Temp,
                NativeArrayOptions.UninitializedMemory);
            NativeArray<int> matchedMembers = new(
                slotCount + 1,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory);
            NativeArray<int> previousSlots = new(
                slotCount + 1,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory);
            NativeArray<byte> visitedSlots = new(
                slotCount + 1,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory);

            try
            {
            // Hungarian 使用一基索引，零号槽位作为每次增广的虚拟起点
            // matchedMembers[slot] 保存当前占用该槽位的成员，一对一约束由它集中维护
            // memberPotentials 和 slotPotentials 保存对偶势，避免反复修改原始代价矩阵
            // minimumCosts 保存当前交替树到每个未访问槽位的最短约化代价
            // previousSlots 记录交替树父边，找到空槽后沿它反向翻转匹配
            for (int member = 1; member <= memberCount; member++)
            {
                // 每轮只加入一个新成员，前面已经建立的匹配作为增广路径起点
                matchedMembers[0] = member;
                for (int slot = 0; slot <= slotCount; slot++)
                {
                    // 临时最短路状态不能跨成员复用，势函数和已有匹配则必须保留
                    minimumCosts[slot] = float.PositiveInfinity;
                    previousSlots[slot] = 0;
                    visitedSlots[slot] = 0;
                }

                int currentSlot = 0;
                do
                {
                    // 访问一个已匹配槽位后，继续从其成员向所有未访问槽位松弛
                    visitedSlots[currentSlot] = 1;
                    int currentMember = matchedMembers[currentSlot];
                    float delta = float.PositiveInfinity;
                    int nextSlot = 0;
                    for (int slot = 1; slot <= slotCount; slot++)
                    {
                        if (visitedSlots[slot] != 0)
                        {
                            continue;
                        }

                        float cost = costMatrix[
                            (currentMember - 1) * slotCount + slot - 1];
                        if (!math.isfinite(cost) || cost < 0f)
                        {
                            return false;
                        }

                        float reducedCost = cost - memberPotentials[currentMember] -
                                            slotPotentials[slot];
                        if (reducedCost < minimumCosts[slot] - 1e-5f)
                        {
                            // 更短边替换父节点，后续增广会沿这条确定路径回溯
                            minimumCosts[slot] = reducedCost;
                            previousSlots[slot] = currentSlot;
                        }

                        // 约化代价相同时优先更小槽位，固定跨平台和重复运行结果
                        if (minimumCosts[slot] < delta - 1e-5f ||
                            (math.abs(minimumCosts[slot] - delta) <= 1e-5f &&
                             (nextSlot == 0 || slot < nextSlot)))
                        {
                            delta = minimumCosts[slot];
                            nextSlot = slot;
                        }
                    }

                    if (nextSlot == 0 || !math.isfinite(delta))
                    {
                        // 有限完整矩阵不应进入此分支，失败时不发布部分匹配
                        return false;
                    }

                    // 势函数只按本轮最小余量推进，保持所有约化代价非负
                    // 已进入交替树的顶点收紧对偶约束，其他槽位同步扣除余量
                    for (int slot = 0; slot <= slotCount; slot++)
                    {
                        if (visitedSlots[slot] != 0)
                        {
                            memberPotentials[matchedMembers[slot]] += delta;
                            slotPotentials[slot] -= delta;
                        }
                        else
                        {
                            minimumCosts[slot] -= delta;
                        }
                    }

                    currentSlot = nextSlot;
                }
                // 空槽意味着增广路径已完成；已占用槽位则继续扩展交替树
                while (matchedMembers[currentSlot] != 0);

                // 从空槽反向翻转父链，使新成员进入匹配且不制造重复槽位
                do
                {
                    int previousSlot = previousSlots[currentSlot];
                    matchedMembers[currentSlot] = matchedMembers[previousSlot];
                    currentSlot = previousSlot;
                }
                while (currentSlot != 0);
            }

            // 输出先设为无效值，便于检测异常矩阵没有覆盖全部成员的情况
            for (int memberIndex = 0; memberIndex < memberCount; memberIndex++)
            {
                assignments[memberIndex] = -1;
            }

            // 内部结构按槽位保存占用者，公开结果转换为逐成员槽位索引
            for (int slot = 1; slot <= slotCount; slot++)
            {
                int member = matchedMembers[slot];
                if (member > 0 && member <= memberCount)
                {
                    assignments[member - 1] = slot - 1;
                }
            }

            // 只有完整匹配才能被 Formation System 原子写回成员 Buffer
            for (int memberIndex = 0; memberIndex < memberCount; memberIndex++)
            {
                if (assignments[memberIndex] < 0)
                {
                    return false;
                }
            }

            return true;
            }
            finally
            {
                // NativeArray 通过显式 finally 释放，避免 using 变量的只读限制
                memberPotentials.Dispose();
                slotPotentials.Dispose();
                minimumCosts.Dispose();
                matchedMembers.Dispose();
                previousSlots.Dispose();
                visitedSlots.Dispose();
            }
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
