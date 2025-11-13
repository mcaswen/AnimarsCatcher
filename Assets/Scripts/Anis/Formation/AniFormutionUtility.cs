using Unity.Mathematics;

public static class AniFormationUtility
{
    public const int FormationColumnCount = 5; // 队形列数
    public const float FormationHorizontalSpacing = 2f; // 队形水平间距
    public const float FormationBackwardSpacing = 2f; // 队形纵向间距
    public const float ArrivalRadius = 0.6f;

    // 获取对称列索引
    static int GetSymmetricColumnIndex(int columnIndex, int columnCount)
    {
        int half = columnCount / 2;
        
        // 奇数列, 居中对齐
        if ((columnCount & 1) == 1) return columnIndex - half;
        
        // 偶数列
        return columnIndex < half ? columnIndex - half : columnIndex - half + 1;
    }

    // 把 index 映射成“相对 leader 局部坐标”的偏移
    public static float3 CalculateRectangularFormationLocalOffset(int slotIndex, int columnCount, float horizontalSpacing, float backwardSpacing)
    {
        int row = slotIndex / columnCount;       
        int col = slotIndex % columnCount;
        int scol = GetSymmetricColumnIndex(col, columnCount);

        // 局部坐标：+x 为右，+z 为前
        // 要排在目标后方，所以 -(row+1) * backwardSpacing
        return new float3(scol * horizontalSpacing, 0, -(row + 1) * backwardSpacing);
    }

    // 转世界坐标
    public static float3 RotateLocalOffsetToWorld(float3 localOffset, in quaternion leaderRot)
    {
        return math.mul(leaderRot, localOffset); // 旋转向量
    }
}