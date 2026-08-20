using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 统一烘焙拓扑使用的稳定八方向编码
    /// </summary>
    public static class NavigationGridDirections
    {
        // 索引顺序与 NavigationNeighborMask 位布局构成持久数据协议
        // 调整方向顺序会使旧 Bake Asset 的邻接含义失效
        // Burst 路径使用固定分支，避免托管数组和静态初始化
        public static bool TryGetDirectionIndex(
            int deltaX,
            int deltaZ,
            out int directionIndex)
        {
            // 方向索引必须与烘焙 NeighborMask 的八方向编码完全一致
            // 非相邻 Cell 返回失败防止平滑和步进成本误用远距离边
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
            // Burst 路径内使用固定分支表避免托管数组和静态初始化
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
