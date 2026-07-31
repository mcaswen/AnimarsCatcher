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
            int safeColumnCount = math.max(1, columnCount);
            int rowCount = math.max(1, (count + safeColumnCount - 1) / safeColumnCount);
            int row = index / safeColumnCount;
            int column = index % safeColumnCount;

            float x = (column - (safeColumnCount - 1) * 0.5f) * spacing;
            float z = (row - (rowCount - 1) * 0.5f) * spacing;

            uint seed = math.hash(new uint2((uint)randomSeed, (uint)(index + 1)));
            var random = new Random(seed == 0 ? 1u : seed);
            float2 jitter = random.NextFloat2(-0.05f, 0.05f);

            return origin + new float3(x + jitter.x, 0f, z + jitter.y);
        }

        /// <summary>
        /// 从已排序样本计算最近秩百分位数
        /// </summary>
        /// <param name="sortedSamples">按升序排列且非空的样本</param>
        /// <param name="percentile">零到一之间的百分位</param>
        /// <returns>对应最近秩样本值</returns>
        public static double CalculateNearestRankPercentile(
            double[] sortedSamples,
            double percentile)
        {
            int index = math.clamp(
                (int)math.ceil((float)(percentile * sortedSamples.Length)) - 1,
                0,
                sortedSamples.Length - 1);
            return sortedSamples[index];
        }
    }
}
