using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.NetCode.LowLevel.Unsafe
{
    static internal class DefaultBufferSerialization
    {
        public static unsafe void SerializeBufferToStream<T>(
            T serializer,
            [NoAlias]IntPtr baselinePtr, int snapshotOffset,
            [NoAlias]IntPtr changeMaskData, int maskOffsetInBits, int changeMaskBits,
            [NoAlias]IntPtr snapshotDynamicDataPtr, [NoAlias]IntPtr baselineDynamicDataPtr,
            int len, int dynamicSnapshotDataOffset, int dynamicDataSize, int maskSize,
            ref DataStreamWriter writer, in StreamCompressionModel compressionModel)
            where T: unmanaged, IGhostSerializer
        {
            const int IntSize = 4;
            int baseLen = 0;
            int baseOffset = 0;
            if (baselinePtr != IntPtr.Zero)
            {
                baseLen = (int)GhostComponentSerializer.TypeCast<uint>(baselinePtr, snapshotOffset);
                baseOffset = (int)GhostComponentSerializer.TypeCast<uint>(baselinePtr, snapshotOffset+IntSize);
            }

            // 计算动态数据的 ChangeMask
            var dynamicMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(changeMaskBits * len);
            var dynamicMaskBitsPtr = snapshotDynamicDataPtr + dynamicSnapshotDataOffset;

            var dynamicMaskOffset = 0;
            var offset = dynamicSnapshotDataOffset;
            var bOffset = baseOffset;
            if (len == baseLen)
            {
                for (int j = 0; j < len; ++j)
                {
                    CheckDynamicMaskOffset(dynamicMaskOffset, maskSize);
                    serializer.CalculateChangeMask(
                        snapshotDynamicDataPtr + maskSize + offset,
                        baselineDynamicDataPtr + maskSize + bOffset,
                        dynamicMaskBitsPtr, dynamicMaskOffset);
                    offset += dynamicDataSize;
                    bOffset += dynamicDataSize;
                    dynamicMaskOffset += changeMaskBits;
                }
                // 计算是否存在任意变化，并设置动态 Snapshot Mask
                uint anyChangeMask = 0;

                // 清理 ChangeMask 中剩余的位
                var changeMaskLenInBits = changeMaskBits * len;
                var remaining = (changeMaskBits * len)&31;
                if(remaining > 0)
                    GhostComponentSerializer.CopyToChangeMask(snapshotDynamicDataPtr + dynamicSnapshotDataOffset, 0, changeMaskLenInBits, 32-remaining);
                for (int mi = 0; mi < dynamicMaskUints; ++mi)
                {
                    uint changeMaskUint = GhostComponentSerializer.TypeCast<uint>(snapshotDynamicDataPtr + dynamicSnapshotDataOffset, mi*IntSize);
                    anyChangeMask |= (changeMaskUint!=0)?1u:0;
                }
                GhostComponentSerializer.CopyToChangeMask(changeMaskData, anyChangeMask, maskOffsetInBits, 2);
                // Buffer 没有任何变化时可以提前退出，无需通过网络序列化全零数据流
                // 此时 ChangeMask 和 Buffer 内容都不需要写入
                if (anyChangeMask == 0)
                    return;

                // 将位写入 Data Stream
                for (int mi = 0; mi < dynamicMaskUints; ++mi)
                {
                    uint changeMaskUint = GhostComponentSerializer.TypeCast<uint>(snapshotDynamicDataPtr + dynamicSnapshotDataOffset, mi*IntSize);
                    uint changeBaseMaskUint = GhostComponentSerializer.TypeCast<uint>(baselineDynamicDataPtr + baseOffset, mi*IntSize);
                    writer.WritePackedUIntDelta(changeMaskUint, changeBaseMaskUint, compressionModel);
                }
            }
            else
            {
                // 将动态 ChangeMask 全部设为 1
                // var remaining = changeMaskBits * len;
                // while (remaining > 32)
                // {
                //     GhostComponentSerializer.CopyToChangeMask(dynamicMaskBitsPtr, ~0u, dynamicMaskOffset, 32);
                //     dynamicMaskOffset += 32;
                //     remaining -= 32;
                // }
                // if (remaining > 0)
                //     GhostComponentSerializer.CopyToChangeMask(dynamicMaskBitsPtr, (1u<<remaining)-1, dynamicMaskOffset, remaining);
                // // FIXME：按上方方式设置位更为正确，但需要修改接收系统，并会导致其与 v1 Serializer 不兼容
                for (int j = 0; j < maskSize; ++j)
                    GhostComponentSerializer.TypeCast<byte>(dynamicMaskBitsPtr, j) = 0xff;
                // 设置动态 Snapshot Mask
                GhostComponentSerializer.CopyToChangeMask(changeMaskData, 3, maskOffsetInBits, 2);

                baselineDynamicDataPtr = IntPtr.Zero;
                writer.WritePackedUIntDelta((uint)len, (uint)baseLen, compressionModel);

                // 假定全部元素均已变化，因此不写入 ChangeMask，接收端会将其视为全 1
            }
            // 序列化元素内容
            dynamicMaskOffset = 0;
            offset = dynamicSnapshotDataOffset;
            bOffset = baseOffset;
            if (baselineDynamicDataPtr != IntPtr.Zero)
            {
                for (int j = 0; j < len; ++j)
                {
                    var baselineData = baselineDynamicDataPtr + maskSize + bOffset;
                    serializer.Serialize(
                        snapshotDynamicDataPtr + maskSize + offset,
                        baselineData, dynamicMaskBitsPtr, dynamicMaskOffset, ref writer, compressionModel);
                    offset += dynamicDataSize;
                    bOffset += dynamicDataSize;
                    dynamicMaskOffset += changeMaskBits;
                }
            }
            else
            {
                var defaultElementBaseline = stackalloc byte[serializer.SizeInSnapshot];
                UnsafeUtility.MemClear(defaultElementBaseline, serializer.SizeInSnapshot);

                for (int j = 0; j < len; ++j)
                {
                    serializer.Serialize(
                        snapshotDynamicDataPtr + maskSize + offset,
                        (IntPtr)defaultElementBaseline, dynamicMaskBitsPtr, dynamicMaskOffset, ref writer, compressionModel);
                    offset += dynamicDataSize;
                    bOffset += dynamicDataSize;
                    dynamicMaskOffset += changeMaskBits;
                }
            }
        }
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckDynamicMaskOffset(int offset, int sizeInBytes)
        {
            if (offset > sizeInBytes*8)
                throw new InvalidOperationException("writing dynamic mask bits outside out of bound");
        }
    }
}
