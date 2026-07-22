using System;
using System.Diagnostics;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    /// 为 <see cref="UnsafeBitArray" /> 提供 NetCode 所需的扩展操作
    /// 在相应变更合入依赖包之前临时使用
    /// </summary>
    public static class NetcodeBitArrayExtensions
    {
        /// <summary>
        /// 将整个位数组左移，即从索引 0 向 <see cref="UnsafeBitArray.Capacity"/> 方向移动
        /// 丢弃从高位移出的所有位，并将低位新产生的位全部置为 0
        /// </summary>
        /// <param name="bitArray">执行操作的位数组实例</param>
        /// <param name="shiftBits">所有位需要移动的位数</param>
        public static unsafe void ShiftLeftExt(ref this UnsafeBitArray bitArray, int shiftBits)
        {
            if (shiftBits >= bitArray.Capacity)
            {
                bitArray.Clear();
                return;
            }
            CheckShiftArgs(shiftBits);

            var ptrLength = bitArray.Capacity >> 6;

            // 先按完整的 64 位块移动
            {
                var num64BitHops = shiftBits >> 6;
#if ENABLE_UNITY_COLLECTIONS_CHECKS || UNITY_DOTS_DEBUG
                UnityEngine.Debug.Assert(num64BitHops < ptrLength);
#endif
                for (int i = ptrLength - num64BitHops - 1; i >= 0; i--)
                    bitArray.Ptr[i + num64BitHops] = bitArray.Ptr[i];
                // 将低位索引对应的块清零
                for (int i = 0; i < num64BitHops; i++)
                    bitArray.Ptr[i] = 0;
                shiftBits -= num64BitHops * 64;
            }

            // 再移动剩余位，并从高位向低位反向遍历以免覆盖尚未读取的值
            if (shiftBits > 0)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS || UNITY_DOTS_DEBUG
                UnityEngine.Debug.Assert(shiftBits < 64);
#endif
                for (int i = ptrLength - 1; i >= 1; i--)
                {
                    bitArray.Ptr[i] <<= shiftBits;
                    bitArray.Ptr[i] |= bitArray.Ptr[i - 1] >> (64 - shiftBits);
                }

                bitArray.Ptr[0] <<= shiftBits;
            }
        }

        /// <summary>
        /// 将整个位数组右移，即从 <see cref="UnsafeBitArray.Capacity"/> 向索引 0 方向移动
        /// 丢弃从低位移出的所有位，并将高位新产生的位全部置为 0
        /// </summary>
        /// <param name="bitArray">执行操作的位数组实例</param>
        /// <param name="shiftBits">所有位需要移动的位数</param>
        public static unsafe void ShiftRightExt(ref this UnsafeBitArray bitArray, int shiftBits)
        {
            if (shiftBits >= bitArray.Capacity)
            {
                bitArray.Clear();
                return;
            }

            CheckShiftArgs(shiftBits);
            var ptrLength = bitArray.Capacity >> 6;

            // 先按完整的 64 位块移动
            {
                var num64BitHops = shiftBits >> 6;
#if ENABLE_UNITY_COLLECTIONS_CHECKS || UNITY_DOTS_DEBUG
                UnityEngine.Debug.Assert(num64BitHops < ptrLength);
#endif
                for (int i = 0; i < ptrLength - num64BitHops; i++)
                    bitArray.Ptr[i] = bitArray.Ptr[i + num64BitHops];
                // 将高位索引对应的块清零
                for (int i = ptrLength - num64BitHops; i < ptrLength; i++)
                    bitArray.Ptr[i] = 0;
                shiftBits -= num64BitHops * 64;
            }

            // 再移动剩余位
            if (shiftBits > 0)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS || UNITY_DOTS_DEBUG
                UnityEngine.Debug.Assert(shiftBits < 64);
#endif
                for (int i = 0; i < ptrLength - 1; i++)
                {
                    bitArray.Ptr[i] >>= shiftBits;
                    bitArray.Ptr[i] |= bitArray.Ptr[i + 1] << (64 - shiftBits);
                }

                bitArray.Ptr[ptrLength - 1] >>= shiftBits;
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS"), Conditional("UNITY_DOTS_DEBUG")]
        static void CheckShiftArgs(int shiftBits)
        {
            if (shiftBits < 0)
                throw new ArgumentOutOfRangeException($"Shift called with negative bits value {shiftBits}!");
        }

        /// <summary>
        /// 将位数组转换为便于阅读和记录的格式
        /// </summary>
        /// <param name="bitArray">执行操作的位数组实例</param>
        /// <param name="maxFixedStringLength">拼接结果允许使用的最大定长字符串长度</param>
        /// <returns>示例：<c>BitArray[num_bits,length,numTrueBits,indexOfLastTrueBit][10011100-00000000-00100000-00000000-00000000-00000000-00000000-0000000...]</c></returns>
        public static unsafe FixedString4096Bytes ToDecimalFixedStringExt(ref this UnsafeBitArray bitArray, int maxFixedStringLength = 4093)
        {
            var ptrLength = bitArray.Capacity >> 6;
            var lastTrueBitIndex = bitArray.FindLastSetBitExt();
            var numTrueBits = lastTrueBitIndex >= 0 ? bitArray.CountBits(0, lastTrueBitIndex + 1) : 0;
            FixedString32Bytes end = default;
            if (numTrueBits == 0)
                end = ",ZEROS";
            else if (numTrueBits == bitArray.Length)
                end = ",ONES";

            FixedString4096Bytes sb = $"BitArray[bits:{bitArray.Length},len:{ptrLength}ul,num1s:{numTrueBits},last1:{lastTrueBitIndex}{end}][";
            var exitCap = math.min(maxFixedStringLength, sb.Capacity);
            for (var i = 0; i < ptrLength; i++)
            {
                var maxBit = i == ptrLength - 1 && bitArray.Length != bitArray.Capacity ? bitArray.Length % 64 : 64;
                for (int b = 0; b < maxBit; b++)
                {
                    sb.Append((1ul << b & bitArray.Ptr[i]) != 0 ? '1' : '0');
                    if (exitCap - sb.Length <= 5)
                    {
                        sb.Append((FixedString32Bytes) "...");
                        goto doubleBreak;
                    }

                    if (b % 8 == 7) sb.Append(b != 63 ? '_' : '|');
                }
            }

            doubleBreak:
            sb.Append(']');
            return sb;
        }

        /// <summary>
        /// 查找并返回 BitArray 中最后一个置位位的索引
        /// </summary>
        /// <param name="bitArray">要查询的位数组</param>
        /// <returns>未找到置位位时返回 -1</returns>
        public static unsafe int FindLastSetBitExt(ref this UnsafeBitArray bitArray)
        {
            var ptrLength = bitArray.Capacity >> 6;
            var ptrIndex = ptrLength - 1;
            // 数组长度可能未填满最后一个块，因此先单独处理最高索引块
            if (bitArray.Length != bitArray.Capacity)
            {
                var maxIndex = bitArray.Length % 64;
                var leastSignificantMask = (1ul << maxIndex) - 1;
                var mask = bitArray.Ptr[ptrIndex] & leastSignificantMask;
                if (mask != default) return (ptrIndex * 64) + (63 - math.lzcnt(mask));
                ptrIndex--;
            }

            for (; ptrIndex >= 0; ptrIndex--)
            {
                var mask = bitArray.Ptr[ptrIndex];
                if (mask != default) return (ptrIndex * 64) + (63 - math.lzcnt(mask));
            }

            return -1;
        }
    }
}
