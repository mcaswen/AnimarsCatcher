using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AOT;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    /// 代码生成用于配置序列化函数指针的辅助类
    /// </summary>
    /// <typeparam name="TComponentType">此辅助类要序列化的 Unmanaged Buffer</typeparam>
    /// <typeparam name="TSnapshot">包含 <see cref="IBufferElementData"/> 数据的 Snapshot Data 结构体</typeparam>
    /// <typeparam name="TSerializer">实现 <see cref="IGhostSerializer"/> 接口的具体类型</typeparam>
    public static class BufferSerializationHelper<TComponentType, TSnapshot, TSerializer>
        where TComponentType: unmanaged
        where TSnapshot: unmanaged
        where TSerializer: unmanaged, IGhostSerializer
    {
        /// <summary>
        /// 使用 <paramref name="serializer"/> 策略将预序列化的动态 Buffer 数据复制到 Data Stream <paramref name="writer"/>
        /// </summary>
        /// <param name="snapshotData">Snapshot Buffer 数据</param>
        /// <param name="snapshotOffset">Snapshot Buffer 中的当前 Offset</param>
        /// <param name="snapshotStride">应用于每个 Entity 的 Stride</param>
        /// <param name="maskOffsetInBits">ChangeMask 位数组中的 Offset</param>
        /// <param name="changeMaskBits">ChangeMask 位数组</param>
        /// <param name="count">Entity 数量</param>
        /// <param name="baselines">每个 Entity 的 Baseline</param>
        /// <param name="writer">输出 Data Stream</param>
        /// <param name="compressionModel">用于压缩数据流的 Compression Model</param>
        /// <param name="entityStartBit">Data Stream 中起止 Offset 的数组，表示每个 Component 的压缩数据存储位置</param>
        /// <param name="snapshotDynamicDataPtr">Buffer Snapshot 的存储位置</param>
        /// <param name="dynamicSizePerEntity">每个 Entity 写入 Snapshot Buffer 的总 Buffer 大小，单位为字节</param>
        /// <param name="dynamicSnapshotMaxOffset">动态 Snapshot Buffer 容量</param>
        /// <param name="serializer">用于序列化 Buffer 内容的 IGhostSerialized 实例</param>
        public static void PostSerializeBuffers(IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            int maskOffsetInBits, int changeMaskBits, int count, IntPtr baselines, ref DataStreamWriter writer,
            StreamCompressionModel compressionModel, IntPtr entityStartBit, IntPtr snapshotDynamicDataPtr,
            IntPtr dynamicSizePerEntity, int dynamicSnapshotMaxOffset, TSerializer serializer)
        {
            int dynamicDataSize = UnsafeUtility.SizeOf<TSnapshot>();
            if (serializer.SizeInSnapshot == 0)
            {
                for (int i = 0; i < count; ++i)
                {
                    const int IntSize = 4;
                    ref var startuint = ref GhostComponentSerializer.TypeCast<int>(entityStartBit, IntSize*2*i);
                    startuint = writer.Length/IntSize;
                    startuint = ref GhostComponentSerializer.TypeCast<int>(entityStartBit, IntSize*2*i+IntSize);
                    startuint = 0;
                }
                return;
            }
            for (int i = 0; i < count; ++i)
            {
                // 从预序列化 Snapshot 获取元素数量和 Buffer 内容在动态数据历史缓冲区中的 Offset
                int len = GhostComponentSerializer.TypeCast<int>(snapshotData + snapshotStride*i, snapshotOffset);
                int dynamicSnapshotDataOffset = GhostComponentSerializer.TypeCast<int>(snapshotData + snapshotStride*i, snapshotOffset+4);
                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(changeMaskBits, len);
                CheckDynamicDataRange(dynamicSnapshotDataOffset, maskSize, len, dynamicDataSize, dynamicSnapshotMaxOffset);
                SerializeOneBuffer(i, snapshotData, snapshotOffset, snapshotStride, maskOffsetInBits, changeMaskBits, baselines, ref writer,
                    compressionModel, entityStartBit, snapshotDynamicDataPtr, dynamicSizePerEntity, len, ref dynamicSnapshotDataOffset, dynamicDataSize, maskSize,
                    serializer);
            }
        }

        /// <summary>
        /// 使用 <paramref name="serializer"/> 策略将动态 Buffer 内容序列化到 <paramref name="writer"/> 数据流
        /// </summary>
        /// <param name="stateData">指向 <see cref="GhostSerializerState"/> 结构体的指针</param>
        /// <param name="snapshotData">Snapshot Buffer 数据</param>
        /// <param name="snapshotOffset">Snapshot Buffer 中的当前 Offset</param>
        /// <param name="snapshotStride">应用于每个 Entity 的 Stride</param>
        /// <param name="maskOffsetInBits">ChangeMask 位数组中的 Offset</param>
        /// <param name="changeMaskBits">ChangeMask 位数组</param>
        /// <param name="componentData">指向 Chunk Component Data 的指针</param>
        /// <param name="componentDataLen">每个 Buffer 的长度</param>
        /// <param name="count">Entity 数量</param>
        /// <param name="baselines">每个 Entity 的 Baseline</param>
        /// <param name="writer">输出 Data Stream</param>
        /// <param name="compressionModel">用于压缩数据流的 Compression Model</param>
        /// <param name="entityStartBit">Data Stream 中起止 Offset 的数组，表示每个 Component 的压缩数据存储位置</param>
        /// <param name="snapshotDynamicDataPtr">Buffer Snapshot 的存储位置</param>
        /// <param name="dynamicSnapshotDataOffset">动态 Snapshot Buffer 中的当前 Offset</param>
        /// <param name="dynamicSizePerEntity">每个 Entity 写入 Snapshot Buffer 的总 Buffer 大小，单位为字节</param>
        /// <param name="dynamicSnapshotMaxOffset">动态 Snapshot Buffer 容量</param>
        /// <param name="serializer">用于序列化 Buffer 内容的 IGhostSerialized 实例</param>
        public static void SerializeBuffers(IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            int maskOffsetInBits, int changeMaskBits, IntPtr componentData, IntPtr componentDataLen, int count,
            IntPtr baselines, ref DataStreamWriter writer, StreamCompressionModel compressionModel, IntPtr entityStartBit,
            IntPtr snapshotDynamicDataPtr, ref int dynamicSnapshotDataOffset, IntPtr dynamicSizePerEntity,
            int dynamicSnapshotMaxOffset, TSerializer serializer)
        {
            int dynamicDataSize = UnsafeUtility.SizeOf<TSnapshot>();
            int componentStride = UnsafeUtility.SizeOf<TComponentType>();
            ref readonly var serializerState = ref GhostComponentSerializer.TypeCastReadonly<GhostSerializerState>(stateData);
            for (int i = 0; i < count; ++i)
            {
                int len = GhostComponentSerializer.TypeCast<int>(componentDataLen, i*4);
                // 设置元素数量和 Buffer 内容在动态数据历史缓冲区中的 Offset
                GhostComponentSerializer.TypeCast<uint>(snapshotData + snapshotStride*i, snapshotOffset) = (uint)len;
                GhostComponentSerializer.TypeCast<uint>(snapshotData + snapshotStride*i, snapshotOffset+4) = (uint)dynamicSnapshotDataOffset;

                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(changeMaskBits, len);
                CheckDynamicDataRange(dynamicSnapshotDataOffset, maskSize, len, dynamicDataSize, dynamicSnapshotMaxOffset);

                if (len > 0)
                {
                    // 复制 Buffer 内容
                    IntPtr curCompData = GhostComponentSerializer.TypeCast<IntPtr>(componentData, UnsafeUtility.SizeOf<IntPtr>()*i);
                    IntPtr snapshotData1 = snapshotDynamicDataPtr + maskSize;
                    ref readonly var serializerState1 = ref GhostComponentSerializer.TypeCastReadonly<GhostSerializerState>(stateData);
                    for (int i1 = 0; i1 < len; ++i1)
                    {
                        serializer.CopyToSnapshot(serializerState1, snapshotData1 + dynamicSnapshotDataOffset + dynamicDataSize*i1, curCompData + componentStride*i1);
                    }
                }
                SerializeOneBuffer(i,
                    snapshotData, snapshotOffset, snapshotStride,
                    maskOffsetInBits, changeMaskBits, baselines,
                    ref writer, compressionModel, entityStartBit, snapshotDynamicDataPtr,
                    dynamicSizePerEntity, len,
                    ref dynamicSnapshotDataOffset, dynamicDataSize, maskSize, serializer);
            }
        }

        /// <summary>
        /// 使用 <paramref name="serializer"/> 策略将动态 Buffer 内容复制到 Snapshot
        /// </summary>
        /// <param name="stateData">指向 <see cref="GhostSerializerState"/> 结构体的指针</param>
        /// <param name="snapshotData">Snapshot Buffer 数据</param>
        /// <param name="snapshotOffset">Snapshot Buffer 中的当前 Offset</param>
        /// <param name="snapshotStride">每个 Entity 应用于 Snapshot 指针的 Stride</param>
        /// <param name="componentData">指向 Chunk Component Data 的指针</param>
        /// <param name="componentStride">每个 Entity 应用于 Component 指针的 Stride</param>
        /// <param name="count">Entity 数量</param>
        /// <param name="serializer">用于序列化 Buffer 内容的 IGhostSerialized 实例</param>
        public static void CopyBuffersToSnapshot(IntPtr stateData, IntPtr snapshotData, int snapshotOffset,
            int snapshotStride, IntPtr componentData, int componentStride, int count, TSerializer serializer)
        {
            ref readonly var serializerState = ref GhostComponentSerializer.TypeCastReadonly<GhostSerializerState>(stateData);
            for (int i = 0; i < count; ++i)
            {
                serializer.CopyToSnapshot(serializerState, snapshotData + snapshotOffset + snapshotStride*i, componentData + componentStride*i);
            }
        }

        /// <summary>
        /// 使用 <paramref name="serializer"/> 策略从 Snapshot 复制动态 Buffer 内容
        /// </summary>
        /// <param name="stateData">指向 <see cref="GhostSerializerState"/> 结构体的指针</param>
        /// <param name="snapshotData">Snapshot Buffer 数据</param>
        /// <param name="snapshotOffset">Snapshot Buffer 中的当前 Offset</param>
        /// <param name="snapshotStride">每个 Entity 应用于 Snapshot 指针的 Stride</param>
        /// <param name="componentData">指向 Chunk Component Data 的指针</param>
        /// <param name="componentStride">每个 Entity 应用于 Component 指针的 Stride</param>
        /// <param name="count">Entity 数量</param>
        /// <param name="serializer">用于序列化 Buffer 内容的 IGhostSerialized 实例</param>
        public static void CopyBuffersFromSnapshot(IntPtr stateData, IntPtr snapshotData, int snapshotOffset,
            int snapshotStride, IntPtr componentData, int componentStride, int count, TSerializer serializer)
        {
            var deserializerState = GhostComponentSerializer.TypeCast<GhostDeserializerState>(stateData);
            ref var snapshotInterpolationData = ref GhostComponentSerializer.TypeCast<SnapshotData.DataAtTick>(snapshotData);
            deserializerState.SnapshotTick = snapshotInterpolationData.Tick;
            for (int i = 0; i < count; ++i)
            {
                // 对 Buffer 而言，此函数遍历的是 Buffer 中的元素而不是 Entity
                var snapshotBefore = snapshotInterpolationData.SnapshotBefore + snapshotOffset +snapshotStride * i;
                serializer.CopyFromSnapshot(deserializerState, componentData + componentStride*i,
                    snapshotInterpolationData.InterpolationFactor, snapshotInterpolationData.InterpolationFactor,
                    snapshotBefore, snapshotBefore);
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckDynamicDataRange(int dynamicSnapshotDataOffset, int maskSize, int len, int dynamicDataSize, int dynamicSnapshotMaxOffset)
        {
            if ((dynamicSnapshotDataOffset + maskSize + len*dynamicDataSize) > dynamicSnapshotMaxOffset)
                throw new InvalidOperationException("writing snapshot dyanmicdata outside of memory history buffer memory boundary");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckDynamicMaskOffset(int offset, int sizeInBytes)
        {
            if (offset > sizeInBytes*8)
                throw new InvalidOperationException("writing dynamic mask bits outside out of bound");
        }

        const int IntSize = 4;
        const int BaselinesPerEntity = 4;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SerializeOneBuffer(
            int ent, IntPtr snapshotData,
            int snapshotOffset, int snapshotStride,
            int maskOffsetInBits, int changeMaskBits,
            IntPtr baselines,
            ref DataStreamWriter writer, in StreamCompressionModel compressionModel, IntPtr entityStartBit,
            IntPtr snapshotDynamicDataPtr, IntPtr dynamicSizePerEntity,
            int len, ref int dynamicSnapshotDataOffset, int dynamicDataSize, int maskSize,
            TSerializer serializer)
        {
            int PtrSize = UnsafeUtility.SizeOf<IntPtr>();
            var baseline0Ptr = GhostComponentSerializer.TypeCast<IntPtr>(baselines, PtrSize*ent*BaselinesPerEntity);
            var baselineDynamicDataPtr = GhostComponentSerializer.TypeCast<IntPtr>(baselines, PtrSize*(ent*BaselinesPerEntity+3));
            var changeMaskPtr = snapshotData + sizeof(int) + ent * snapshotStride;
            ref var startuint = ref GhostComponentSerializer.TypeCast<int>(entityStartBit, IntSize*2*ent);
            startuint = writer.Length/IntSize;

            DefaultBufferSerialization.SerializeBufferToStream(
                serializer,
                baseline0Ptr, snapshotOffset,
                changeMaskPtr, maskOffsetInBits, changeMaskBits,
                snapshotDynamicDataPtr, baselineDynamicDataPtr, len, dynamicSnapshotDataOffset,
                dynamicDataSize, maskSize, ref writer, compressionModel);

            var dynamicSize = GhostComponentSerializer.SnapshotSizeAligned(maskSize + dynamicDataSize * len);
            GhostComponentSerializer.TypeCast<int>(dynamicSizePerEntity, ent*IntSize) += dynamicSize;
            dynamicSnapshotDataOffset += dynamicSize;
            ref var sbit = ref GhostComponentSerializer.TypeCast<int>(entityStartBit, IntSize*2*ent+IntSize);
            sbit = writer.LengthInBits - startuint*32;
            var missing = 32-writer.LengthInBits&31;
            if (missing < 32)
                writer.WriteRawBits(0, missing);
        }
    }
}
