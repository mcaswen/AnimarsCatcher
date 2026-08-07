using AnimarsCatcher.Gameplay.Contracts;
using Unity.Mathematics;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation.Harness
{
    /// <summary>
    /// 提供与 Scene 和 World 无关的基准生成与统计算法
    /// </summary>
    public static class LegacyNavigationBenchmarkAlgorithms
    {
        /// <summary>
        /// 按稳定网格和固定种子计算 Ani 初始位置
        /// </summary>
        /// <param name="index">从零开始的 Ani 索引</param>
        /// <param name="count">本次运行的 Ani 总数</param>
        /// <param name="columnCount">生成网格列数</param>
        /// <param name="spacing">相邻 Ani 的基础间距</param>
        /// <param name="origin">生成网格中心</param>
        /// <param name="randomSeed">固定随机种子</param>
        /// <returns>带稳定微小扰动的世界位置</returns>
        public static float3 CalculateSpawnPosition(
            int index,
            int count,
            int columnCount,
            float spacing,
            float3 origin,
            int randomSeed)
        {
            return NavigationBenchmarkInputAlgorithms.CalculateSpawnPosition(
                index,
                count,
                columnCount,
                spacing,
                origin,
                randomSeed);
        }

    }
}
