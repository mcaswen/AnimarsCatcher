using Unity.Mathematics;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 定义阵型布局参数并提供局部槽位到世界偏移的换算
    /// </summary>
    public static class AniFormationUtility
    {
        // Picker 跟随时相对玩家向后偏移的固定距离
        public const float PickerFollowBackOffset = 1.5f;

        // Blaster 跟随时按攻击范围计算后排距离
        public const float BlasterFollowBackFactor = 0.5f;

        // Blaster 寻敌时按攻击范围计算与目标的后撤距离
        public const float BlasterFindBackFactor = 0.5f;

        // Blaster 移动到点击点时按攻击范围计算后排距离
        public const float BlasterMoveToBackFactor = 0.5f;

        public const int FormationColumnCount = 8;
        public const float FormationHorizontalSpacing = 1.8f;
        public const float FormationBackwardSpacing  = 2.5f;

        // 到达半径避免所有成员争抢同一个精确坐标
        public const float ArrivalRadius = 0.7f;

        /// <summary>
        /// 按行列布局计算槽位相对阵型中心的局部偏移
        /// </summary>
        /// <param name="slotIndex">从零开始的稳定槽位索引</param>
        /// <param name="columnCount">每行容纳的槽位数</param>
        /// <param name="horizontalSpacing">同一行的水平间距</param>
        /// <param name="backwardSpacing">相邻行的后向间距</param>
        /// <returns>阵型局部空间中的槽位偏移</returns>
        public static float3 CalculateRectangularFormationLocalOffset(
            int slotIndex,
            int columnCount,
            float horizontalSpacing,
            float backwardSpacing)
        {
            int row = slotIndex / columnCount;
            int column = slotIndex % columnCount;

            float x = (column - (columnCount - 1) * 0.5f) * horizontalSpacing;

            // 后续行沿阵型后方向排列
            float z = -row * backwardSpacing;

            return new float3(x, 0f, z);
        }

        /// <summary>
        /// 使用阵型旋转把局部槽位偏移转换到世界空间
        /// </summary>
        /// <param name="localOffset">阵型局部偏移</param>
        /// <param name="rotation">阵型世界旋转</param>
        /// <returns>旋转后的世界空间偏移</returns>
        public static float3 RotateLocalOffsetToWorld(float3 localOffset, quaternion rotation)
        {
            return math.mul(rotation, localOffset);
        }
    }
}
