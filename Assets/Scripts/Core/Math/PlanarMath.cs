using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace AnimarsCatcher.Core
{
    /// <summary>
    /// 提供以世界 XZ 平面为约束的无状态向量计算
    /// </summary>
    public static class PlanarMath
    {
        /// <summary>
        /// 清除向量的垂直分量
        /// </summary>
        /// <param name="value">待投影向量</param>
        /// <returns>位于 XZ 平面的向量</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 FlattenY(float3 value)
        {
            value.y = 0f;
            return value;
        }

        /// <summary>
        /// 尝试将向量归一化到 XZ 平面
        /// </summary>
        /// <param name="value">待归一化向量</param>
        /// <param name="normalized">成功时返回单位方向，失败时返回零向量</param>
        /// <param name="minimumLengthSquared">认定为零向量的长度平方阈值</param>
        /// <returns>输入为有限且长度足够的 XZ 向量时返回 true</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryNormalizeXZ(
            float3 value,
            out float3 normalized,
            float minimumLengthSquared = 1e-6f)
        {
            value.y = 0f;
            if (!VectorMath.IsFinite(value) || math.lengthsq(value) <= minimumLengthSquared)
            {
                normalized = float3.zero;
                return false;
            }

            normalized = math.normalize(value);
            return true;
        }

        /// <summary>
        /// 将向量归一化到 XZ 平面，输入无效时返回指定回退方向
        /// </summary>
        /// <param name="value">待归一化向量</param>
        /// <param name="fallback">输入为零或非有限值时使用的回退方向</param>
        /// <param name="minimumLengthSquared">认定为零向量的长度平方阈值</param>
        /// <returns>有限的 XZ 单位方向</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 NormalizeXZOrDefault(
            float3 value,
            float3 fallback,
            float minimumLengthSquared = 1e-6f)
        {
            if (TryNormalizeXZ(value, out float3 normalized, minimumLengthSquared))
            {
                return normalized;
            }

            return TryNormalizeXZ(fallback, out normalized, minimumLengthSquared)
                ? normalized
                : float3.zero;
        }
    }
}
