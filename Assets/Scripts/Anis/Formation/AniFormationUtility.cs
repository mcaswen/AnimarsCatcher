using Unity.Mathematics;

public static class AniFormationUtility
{
    // Follow：Picker 在玩家身后 2 米
    public const float PickerFollowBackOffset = 1.5f;

    // Follow：Blaster 在玩家身后 1 * AttackRange
    public const float BlasterFollowBackFactor = 0.5f;

    // Find：Blaster 在相对位置反方向 0.8 * AttackRange
    public const float BlasterFindBackFactor = 0.5f;

    // MoveTo：Blaster 在点击点相对位置反方向 0.8 * AttackRange
    public const float BlasterMoveToBackFactor = 0.5f;

    public const int FormationColumnCount = 8;
    public const float FormationHorizontalSpacing = 1.8f;
    public const float FormationBackwardSpacing  = 2.5f;

    // 阵列的“到达半径”，避免所有人挤一个精确点
    public const float ArrivalRadius = 0.7f;

    public static float3 CalculateRectangularFormationLocalOffset(
        int slotIndex,
        int columnCount,
        float horizontalSpacing,
        float backwardSpacing)
    {
        int row = slotIndex / columnCount;
        int column = slotIndex % columnCount;

        float x = (column - (columnCount - 1) * 0.5f) * horizontalSpacing;
        float z = -row * backwardSpacing; // 队列往后排

        return new float3(x, 0f, z);
    }

    public static float3 RotateLocalOffsetToWorld(float3 localOffset, quaternion rotation)
    {
        return math.mul(rotation, localOffset);
    }
}
