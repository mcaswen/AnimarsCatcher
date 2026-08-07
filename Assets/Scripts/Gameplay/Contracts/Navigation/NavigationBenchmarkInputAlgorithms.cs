using Unity.Mathematics;

namespace AnimarsCatcher.Gameplay.Contracts
{
    /// <summary>
    /// 生成跨导航后端共用的确定性 Benchmark 输入
    /// </summary>
    public static class NavigationBenchmarkInputAlgorithms
    {
        /// <summary>
        /// 按稳定索引生成居中网格起点和可复现抖动
        /// </summary>
        public static float3 CalculateSpawnPosition(
            int index,
            int count,
            int columnCount,
            float spacing,
            float3 origin,
            int randomSeed)
        {
            // 列数和行数都钳制到正数，保证异常配置仍生成可回放的网格起点
            int safeColumnCount = math.max(1, columnCount);
            int rowCount = math.max(1, (count + safeColumnCount - 1) / safeColumnCount);
            int row = index / safeColumnCount;
            int column = index % safeColumnCount;

            float x = (column - (safeColumnCount - 1) * 0.5f) * spacing;
            float z = (row - (rowCount - 1) * 0.5f) * spacing;

            // Hash 将种子与稳定索引绑定，单个成员数量变化不会重排其他成员抖动
            uint seed = math.hash(new uint2((uint)randomSeed, (uint)(index + 1)));
            var random = new Random(seed == 0 ? 1u : seed);
            float2 jitter = random.NextFloat2(-0.05f, 0.05f);
            return origin + new float3(x + jitter.x, 0f, z + jitter.y);
        }
    }
}
