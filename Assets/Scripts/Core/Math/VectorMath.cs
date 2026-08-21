using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace AnimarsCatcher.Core
{
    /// <summary>
    /// 提供可供各玩法模块复用的三维向量计算
    /// </summary>
    public static class VectorMath
    {
        /// <summary>
        /// 判断三维向量的每个分量是否为有限值
        /// </summary>
        /// <param name="value">待检查向量</param>
        /// <returns>全部分量有限时返回 true</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsFinite(float3 value)
        {
            return math.all(math.isfinite(value));
        }

        /// <summary>
        /// 按最大变化量将当前向量移向目标向量
        /// </summary>
        /// <param name="current">当前向量</param>
        /// <param name="target">目标向量</param>
        /// <param name="maximumDelta">本次允许的最大变化量</param>
        /// <returns>限制变化量后的向量</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 MoveTowards(float3 current, float3 target, float maximumDelta)
        {
            float distance = math.distance(current, target);
            if (distance <= maximumDelta || distance <= 1e-5f)
            {
                return target;
            }

            return current + (target - current) * (maximumDelta / distance);
        }
    }
}
