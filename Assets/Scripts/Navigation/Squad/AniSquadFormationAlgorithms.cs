using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 提供阵型宽度、角色槽位、成员分配和槽位坐标等纯计算方法
    /// </summary>
    public static class AniSquadFormationAlgorithms
    {
        /// <summary>
        /// 根据阵型类型、成员数和配置上限确定每排人数
        /// </summary>
        /// <param name="kind">阵型类型</param>
        /// <param name="memberCount">当前成员数量</param>
        /// <param name="configuredColumns">紧凑矩形的配置列数</param>
        /// <returns>实际列数，范围为 1 到成员数量</returns>
        public static int CalculateColumnCount(
            AniSquadFormationKind kind,
            int memberCount,
            int configuredColumns)
        {
            int count = math.max(1, memberCount);
            if (kind == AniSquadFormationKind.Column)
            {
                // 纵队始终只有一列，不受外部配置影响
                return 1;
            }

            // 列数不超过成员数，避免布局中出现整列空位
            return math.clamp(configuredColumns, 1, count);
        }

        /// <summary>
        /// 根据队伍前方的可用宽度，计算当前最多可以并排行进的成员数
        /// </summary>
        /// <param name="kind">当前阵型类型</param>
        /// <param name="memberCount">当前有效成员数量</param>
        /// <param name="usableWidth">扣除两侧安全距离后，前方真正可用的宽度</param>
        /// <param name="maximumAgentDiameter">队伍最大成员直径</param>
        /// <param name="horizontalGap">相邻列之间的额外间距</param>
        /// <returns>适合当前通道的列数，范围为 1 到成员数量</returns>
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
        /// 指定各排更适合的成员职责：Picker 在前，Blaster 在后
        /// </summary>
        /// <param name="slotIndex">槽位索引</param>
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
        /// 使用 Hungarian 算法为每名成员分配唯一槽位，并尽量降低全队换位成本
        /// </summary>
        /// <param name="costMatrix">每名成员前往每个槽位的代价矩阵，按成员逐行存放</param>
        /// <param name="memberCount">需要分配的成员数量</param>
        /// <param name="slotCount">可用槽位数量，必须不少于成员数量</param>
        /// <param name="assignments">输出每名成员对应的槽位索引</param>
        /// <returns>成功为所有成员分配不同槽位时返回 true</returns>
        public static bool TrySolveMinimumCostAssignment(
            NativeArray<float> costMatrix,
            int memberCount,
            int slotCount,
            NativeArray<int> assignments)
        {
            // 每名成员都需要独立槽位，因此槽位数不能少于成员数
            // 求解过程只写 assignments，不会修改调用方传入的代价矩阵
            // 先检查输入再申请临时内存，错误调用可以尽早返回
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
            // 这个 Hungarian 实现从索引 1 开始，索引 0 用作每轮查找的虚拟起点
            // matchedMembers 记录槽位当前属于谁，用它保证一名成员对应一个槽位
            // 两组 potentials 保存算法的对偶势，因此不需要修改原始代价矩阵
            // minimumCosts 记录搜索树到各个未访问槽位的最低剩余代价
            // previousSlots 记录搜索路径，找到空槽后可沿路径反向更新匹配
            for (int member = 1; member <= memberCount; member++)
            {
                // 每轮为一名新成员找位置，并在必要时调整之前的匹配
                matchedMembers[0] = member;
                for (int slot = 0; slot <= slotCount; slot++)
                {
                    // 每名成员都要重新开始路径搜索，但已有匹配和势函数会继续使用
                    minimumCosts[slot] = float.PositiveInfinity;
                    previousSlots[slot] = 0;
                    visitedSlots[slot] = 0;
                }

                int currentSlot = 0;
                do
                {
                    // 如果槽位已有成员，就从那名成员继续检查其他尚未访问的槽位
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
                            // 找到成本更低的路线时更新父节点，之后会沿这条路线回溯
                            minimumCosts[slot] = reducedCost;
                            previousSlots[slot] = currentSlot;
                        }

                        // 成本相同时选择索引更小的槽位，保证重复运行得到相同结果
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
                        // 完整且数值有效的矩阵不应走到这里；异常时直接失败，不输出半套结果
                        return false;
                    }

                    // 按本轮最小余量更新势函数，使后续比较的剩余代价始终不为负
                    // 搜索树中的节点收紧约束，其他候选槽位则同步扣除这段余量
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
                // 找到空槽即可结束本轮；如果槽位已被占用，就继续扩展搜索路径
                while (matchedMembers[currentSlot] != 0);

                // 从空槽沿父链反向更新归属，让新成员加入且不会与别人共用槽位
                do
                {
                    int previousSlot = previousSlots[currentSlot];
                    matchedMembers[currentSlot] = matchedMembers[previousSlot];
                    currentSlot = previousSlot;
                }
                while (currentSlot != 0);
            }

            // 先把输出设为无效值，便于发现没有为所有成员完成分配的异常情况
            for (int memberIndex = 0; memberIndex < memberCount; memberIndex++)
            {
                assignments[memberIndex] = -1;
            }

            // 内部结果按槽位记录成员，这里再转换为每名成员对应的槽位
            for (int slot = 1; slot <= slotCount; slot++)
            {
                int member = matchedMembers[slot];
                if (member > 0 && member <= memberCount)
                {
                    assignments[member - 1] = slot - 1;
                }
            }

            // 只有完整分配才交给阵型系统，避免成员缓冲区出现一半新、一半旧的槽位
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
                // 在 finally 中统一释放临时数组，确保任何返回路径都不会泄漏内存
                memberPotentials.Dispose();
                slotPotentials.Dispose();
                minimumCosts.Dispose();
                matchedMembers.Dispose();
                previousSlots.Dispose();
                visitedSlots.Dispose();
            }
        }

        /// <summary>
        /// 计算一个槽位相对队伍中心的位置，并让每排成员保持居中
        /// </summary>
        /// <param name="slotIndex">槽位索引</param>
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

            // 每一排按实际人数居中，最后一排人数不足时也不会偏向一侧
            float x = (column - (rowCount - 1) * 0.5f) * horizontalSpacing;
            float meanRowOffset = CalculateMeanRowOffset(
                count,
                columns,
                longitudinalSpacing);
            float z = -row * longitudinalSpacing - meanRowOffset;
            if (kind == AniSquadFormationKind.Column)
            {
                // 纵队的所有成员都站在中线上，只沿前后方向排开
                x = 0f;
            }

            return new float3(x, 0f, z);
        }

        private static float CalculateMeanRowOffset(
            int memberCount,
            int columns,
            float longitudinalSpacing)
        {
            int rowCount = (memberCount + columns - 1) / columns;
            float sum = 0f;

            // 按每排实际人数计算纵向中心，使人数不足的最后一排也参与整体居中
            for (int row = 0; row < rowCount; row++)
            {
                int countInRow = math.min(columns, memberCount - row * columns);
                sum += countInRow * (-row * longitudinalSpacing);
            }

            return sum / math.max(1, memberCount);
        }
    }
}
