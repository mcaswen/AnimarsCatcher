using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 在烘焙和运行时之间统一八方向邻接关系的编号
    /// </summary>
    public static class NavigationGridDirections
    {
        // 这些编号与 NavigationNeighborMask 的位顺序一一对应
        // 修改顺序会改变旧烘焙资产中邻接位的含义，因此必须保持不变
        // 这里使用分支而不是托管数组，确保代码可以在 Burst 中运行
        public static bool TryGetDirectionIndex(
            int deltaX,
            int deltaZ,
            out int directionIndex)
        {
            // 只接受周围八个相邻格子，避免路径平滑或成本计算把远处格子当成一步
            directionIndex = -1;
            if (deltaX == 0 && deltaZ == 1) directionIndex = 0;
            else if (deltaX == 1 && deltaZ == 1) directionIndex = 1;
            else if (deltaX == 1 && deltaZ == 0) directionIndex = 2;
            else if (deltaX == 1 && deltaZ == -1) directionIndex = 3;
            else if (deltaX == 0 && deltaZ == -1) directionIndex = 4;
            else if (deltaX == -1 && deltaZ == -1) directionIndex = 5;
            else if (deltaX == -1 && deltaZ == 0) directionIndex = 6;
            else if (deltaX == -1 && deltaZ == 1) directionIndex = 7;
            return directionIndex >= 0;
        }

        public static void GetDirection(int directionIndex, out int deltaX, out int deltaZ)
        {
            // 固定分支既保留编号顺序，也避免 Burst 依赖托管数组
            switch (directionIndex)
            {
                case 0: deltaX = 0; deltaZ = 1; return;
                case 1: deltaX = 1; deltaZ = 1; return;
                case 2: deltaX = 1; deltaZ = 0; return;
                case 3: deltaX = 1; deltaZ = -1; return;
                case 4: deltaX = 0; deltaZ = -1; return;
                case 5: deltaX = -1; deltaZ = -1; return;
                case 6: deltaX = -1; deltaZ = 0; return;
                default: deltaX = -1; deltaZ = 1; return;
            }
        }

        public static int2 GetOffset(int directionIndex)
        {
            GetDirection(directionIndex, out int deltaX, out int deltaZ);
            return new int2(deltaX, deltaZ);
        }
    }
}
