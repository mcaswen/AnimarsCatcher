using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace AnimarsCatcher.Core
{
    /// <summary>
    /// 提供不依赖场景和 World 的统计计算
    /// </summary>
    public static class StatisticsMath
    {
        /// <summary>
        /// 从升序样本计算最近秩百分位数
        /// </summary>
        /// <param name="sortedSamples">按升序排列的样本</param>
        /// <param name="percentile">百分位，超出范围时按边界处理</param>
        /// <returns>对应样本值，空输入返回零</returns>
        public static double CalculateNearestRankPercentile(
            double[] sortedSamples,
            double percentile)
        {
            if (sortedSamples == null || sortedSamples.Length == 0)
            {
                return 0.0;
            }

            int index = math.clamp(
                (int)math.ceil((float)(math.clamp(percentile, 0.0, 1.0) * sortedSamples.Length)) - 1,
                0,
                sortedSamples.Length - 1);
            return sortedSamples[index];
        }

        /// <summary>
        /// 计算位置集合中任意两点的最小间距
        /// </summary>
        /// <param name="positions">待比较的位置集合</param>
        /// <returns>最小间距，少于两个位置时返回零</returns>
        public static float CalculateMinimumPairwiseDistance(IReadOnlyList<float3> positions)
        {
            if (positions == null || positions.Count < 2)
            {
                return 0f;
            }

            float minimumDistance = float.PositiveInfinity;
            for (int first = 0; first < positions.Count; first++)
            {
                for (int second = first + 1; second < positions.Count; second++)
                {
                    minimumDistance = math.min(
                        minimumDistance,
                        math.distance(positions[first], positions[second]));
                }
            }

            return math.isfinite(minimumDistance) ? minimumDistance : 0f;
        }
    }
}
