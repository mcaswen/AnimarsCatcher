using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine.Assertions;

namespace Unity.NetCode.LowLevel.Unsafe
{
    /// <summary>
    /// 包含编写自定义 Chunk Serializer 所需的辅助方法
    /// 关于自定义 Chunk Serializer 函数指针的用途，请参阅 <see cref="GhostPrefabCustomSerializer"/>
    /// </summary>
    public static unsafe class CustomGhostSerializerHelpers
    {
        /// <summary>
        /// 将整个 Chunk 中从 <see cref="GhostPrefabCustomSerializer.Context.startIndex"/> 开始，
        /// 到 <see cref="GhostPrefabCustomSerializer.Context.endIndex"/> 结束的 Component Data 复制到 Snapshot Buffer
        /// </summary>
        /// <param name="chunk">源 Chunk</param>
        /// <param name="context">序列化上下文</param>
        /// <param name="typeHandles">Component TypeHandle 集合</param>
        /// <param name="index">对应的 <see cref="GhostCollectionComponentIndex"/> Buffer</param>
        /// <param name="snapshotData">Snapshot Buffer 数据存储区</param>
        /// <param name="snapshotOffset">Component Data 存储位置的字节 Offset</param>
        /// <param name="serializer">当前使用的 Serializer</param>
        /// <typeparam name="T">Unmanaged Component 类型</typeparam>
        public static void CopyComponentToSnapshot<T>(
            this T serializer,
            ArchetypeChunk chunk,
            ref GhostPrefabCustomSerializer.Context context,
            DynamicComponentTypeHandle* typeHandles,
            in GhostCollectionComponentIndex index,
            IntPtr snapshotData,
            ref int snapshotOffset) where T: unmanaged, IGhostSerializer
        {
            if(Burst.CompilerServices.Hint.Unlikely(!serializer.HasGhostFields))
                return;
            var data = (IntPtr)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandles[index.ComponentIndex], index.ComponentSize).GetUnsafeReadOnlyPtr();
            var snapshot = snapshotData + snapshotOffset;
            for (int ent = context.startIndex; ent < context.endIndex; ++ent)
            {
                serializer.CopyToSnapshot(context.serializerState, snapshot, data + index.ComponentSize*ent);
                snapshot += context.snapshotStride;
            }
            snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(index.SnapshotSize);
            Assert.IsTrue(snapshotOffset <= context.snapshotStride);
        }

        /// <summary>
        /// 将 Child Component 的单份 Component Data 复制到 Snapshot Buffer
        /// </summary>
        /// <param name="serializer">要使用的 Serializer</param>
        /// <param name="chunk">要复制的 Chunk</param>
        /// <param name="indexInChunk">Chunk 中的索引</param>
        /// <param name="context">序列化上下文</param>
        /// <param name="typeHandles">Component TypeHandle 集合</param>
        /// <param name="index"><see cref="GhostCollectionComponentIndex>"/> 集合</param>
        /// <param name="snapshotData">Snapshot Buffer 数据</param>
        /// <param name="snapshotOffset">相对 Snapshot Buffer 起始位置的 Offset</param>
        /// <typeparam name="T">Component 类型</typeparam>
        public static void CopyChildComponentToSnapshot<T>(
            this T serializer,
            ArchetypeChunk chunk,
            int indexInChunk,
            ref GhostPrefabCustomSerializer.Context context,
            DynamicComponentTypeHandle* typeHandles,
            in GhostCollectionComponentIndex index,
            IntPtr snapshotData, ref int snapshotOffset) where T: unmanaged, IGhostSerializer
        {
            if(Burst.CompilerServices.Hint.Unlikely(!serializer.HasGhostFields))
                return;
            var data = (IntPtr)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandles[index.ComponentIndex], index.ComponentSize).GetUnsafeReadOnlyPtr();
            serializer.CopyToSnapshot(context.serializerState, snapshotData + snapshotOffset, data + index.ComponentSize*indexInChunk);
            snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(index.SnapshotSize);
            Assert.IsTrue(snapshotOffset <= context.snapshotStride);
        }

        /// <summary>
        /// 将 Chunk 中指定 <see cref="DynamicComponentTypeHandle"/> 的所有 Buffer 复制到 Snapshot Buffer，
        /// 范围从 <see cref="GhostPrefabCustomSerializer.Context.startIndex"/> 开始，
        /// 到 <see cref="GhostPrefabCustomSerializer.Context.endIndex"/> 结束
        /// </summary>
        /// <param name="serializer">要使用的 Serializer</param>
        /// <param name="chunk">要复制的 Chunk</param>
        /// <param name="context">序列化上下文</param>
        /// <param name="typeHandles">Component TypeHandle 集合</param>
        /// <param name="index"><see cref="GhostCollectionComponentIndex>"/> 集合</param>
        /// <param name="snapshotData">Snapshot Buffer 数据</param>
        /// <param name="snapshotOffset">相对 Snapshot Buffer 起始位置的 Offset</param>
        /// <param name="dynamicSnapshotDataOffset">Buffer Data 在动态 Snapshot Data Buffer 中的 Offset</param>
        /// <typeparam name="T">Buffer 类型</typeparam>
        public static void CopyBufferToSnapshot<T>(
            this T serializer,
            ArchetypeChunk chunk, ref GhostPrefabCustomSerializer.Context context,
            DynamicComponentTypeHandle* typeHandles,
            in GhostCollectionComponentIndex index,
            IntPtr snapshotData,
            ref int snapshotOffset, ref int dynamicSnapshotDataOffset) where T: unmanaged, IGhostSerializer
        {
            if(Burst.CompilerServices.Hint.Unlikely(!serializer.HasGhostFields))
                return;
            var bufAccessor = chunk.GetUntypedBufferAccessor(ref typeHandles[index.ComponentIndex]);
            var snapshot = snapshotData + snapshotOffset;
            for (int ent = context.startIndex; ent < context.endIndex; ++ent)
            {
                var bufData = (IntPtr)bufAccessor.GetUnsafeReadOnlyPtrAndLength(ent, out var bufLen);
                CopyBufferDataToSnapshot(context, ref dynamicSnapshotDataOffset, index.ComponentSize, index.SnapshotSize,
                    serializer, snapshot, bufData, bufLen);
                snapshot += context.snapshotStride;
            }
            snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
            Assert.IsTrue(snapshotOffset <= context.snapshotStride);
        }

        /// <summary>
        /// 将 Child Entity 上指定 <see cref="DynamicComponentTypeHandle"/> 的单个 Buffer 复制到 Snapshot Buffer
        /// </summary>
        /// <param name="serializer">要使用的 Serializer</param>
        /// <param name="chunk">要复制的 Chunk</param>
        /// <param name="indexInChunk">Chunk 中的索引</param>
        /// <param name="context">序列化上下文</param>
        /// <param name="typeHandles">Component TypeHandle 集合</param>
        /// <param name="index"><see cref="GhostCollectionComponentIndex>"/> 集合</param>
        /// <param name="snapshotData">Snapshot Buffer 数据</param>
        /// <param name="snapshotOffset">相对 Snapshot Buffer 起始位置的 Offset</param>
        /// <param name="dynamicSnapshotOffset">Buffer Data 在动态 Snapshot Data Buffer 中的 Offset</param>
        /// <typeparam name="T">Buffer 类型</typeparam>
        public static void CopyChildBufferToSnapshot<T>(
            this T serializer,
            ArchetypeChunk chunk, int indexInChunk,
            ref GhostPrefabCustomSerializer.Context context,
            DynamicComponentTypeHandle* typeHandles,
            in GhostCollectionComponentIndex index,
            IntPtr snapshotData,ref int snapshotOffset, ref int dynamicSnapshotOffset)
            where T: unmanaged, IGhostSerializer
        {
            if(Burst.CompilerServices.Hint.Unlikely(!serializer.HasGhostFields))
                return;
            var bufAccessor = chunk.GetUntypedBufferAccessor(ref typeHandles[index.ComponentIndex]);
            var snapshot = snapshotData + snapshotOffset;
            var bufData = (IntPtr)bufAccessor.GetUnsafeReadOnlyPtrAndLength(indexInChunk, out var bufLen);
            CopyBufferDataToSnapshot(context, ref dynamicSnapshotOffset,
                index.ComponentSize, index.SnapshotSize, serializer,
                snapshot, bufData, bufLen);
            snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
            Assert.IsTrue(snapshotOffset <= context.snapshotStride);
        }

        private static void CopyBufferDataToSnapshot<T>(GhostPrefabCustomSerializer.Context context,
            ref int dynamicSnapshotOffset, int componentSize, int snapshotSize, T serializer,
            IntPtr snapshot, IntPtr bufData, int bufLen) where T : unmanaged, IGhostSerializer
        {
            if(Burst.CompilerServices.Hint.Unlikely(!serializer.HasGhostFields))
                return;
            var dynamicSnapshot = context.snapshotDynamicDataPtr + dynamicSnapshotOffset;
            // 设置元素数量和 Buffer 内容在动态数据历史缓冲区中的 Offset
            *(uint*)snapshot = (uint)bufLen;
            *(uint*)(snapshot + 4) = (uint)dynamicSnapshotOffset;
            if (bufLen > 0)
            {
                // 复制 Buffer 内容，暂时跳过稍后处理的 ChangeMask
                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(serializer.ChangeMaskSizeInBits, bufLen);
                dynamicSnapshot += maskSize;
                for (int el = 0; el < bufLen; ++el)
                {
                    serializer.CopyToSnapshot(context.serializerState, dynamicSnapshot + snapshotSize * el, bufData + componentSize * el);
                }

                var dynamicSize = GhostComponentSerializer.SnapshotSizeAligned(maskSize + snapshotSize * bufLen);
                dynamicSnapshotOffset += dynamicSize;
                Assert.IsTrue(dynamicSnapshotOffset <= context.dynamicDataCapacity);
            }
        }


        /// <summary>
        /// 将指定 <see cref="DynamicComponentTypeHandle"/> 的全部 Enable Bit 状态复制到 Snapshot Buffer
        /// </summary>
        /// <param name="chunk">源 Chunk</param>
        /// <param name="startIndex">起始 Entity 索引</param>
        /// <param name="endIndex">结束 Entity 索引，不包含该索引</param>
        /// <param name="snapshotStride">指定 Archetype 的 Snapshot Data 字节 Stride，即大小</param>
        /// <param name="componentTypeHandle">要提取的 Component TypeHandle</param>
        /// <param name="enableMasks">Snapshot Enable Bit Mask 数组</param>
        /// <param name="maskOffset">数组中的位 Offset</param>
        public static void CopyEnableBits(ArchetypeChunk chunk, int startIndex, int endIndex,
            int snapshotStride, ref DynamicComponentTypeHandle componentTypeHandle, byte* enableMasks,
            ref int maskOffset)
        {
            var array = chunk.GetEnableableBits(ref componentTypeHandle);
            var bitArray = new UnsafeBitArray(&array, 2 * sizeof(ulong));
            var entityMask = ((uint*)enableMasks) + maskOffset / 32;
            var bitOffset = maskOffset % 32;
            snapshotStride /= 4;
            for (int ent = startIndex; ent < endIndex; ++ent)
            {
                if (bitOffset == 0)
                    *entityMask = 0;
                var isSetOnServer = bitArray.IsSet(ent);
                if (isSetOnServer)
                    *entityMask |= 1U << bitOffset;
                entityMask += snapshotStride;
            }
            ++maskOffset;
        }
    }

    /// <summary>
    /// 所有实现 <see cref="IGhostSerializer"/> 接口的 Unmanaged 类型所使用的扩展方法
    /// </summary>
    static public class GhostCustomSerializerExtensions
    {
        /// <summary>
        /// 使用单个 Baseline 将指定 Component 序列化到 Data Stream
        /// </summary>
        /// <param name="serializer">Serializer 实例</param>
        /// <param name="snapshot">Snapshot Buffer 数据</param>
        /// <param name="baseline">用于计算差值的 Baseline，可以是零 Baseline</param>
        /// <param name="changeMaskData">ChangeMask 位 Buffer</param>
        /// <param name="startOffset">BitMask 起始 Offset</param>
        /// <param name="snapshotOffset">数据起始 Offset</param>
        /// <param name="writer">数据写入器</param>
        /// <param name="compressionModel">压缩模型</param>
        /// <param name="sendComponent">是否应发送此 Component</param>
        /// <typeparam name="TSerializer">Serializer 类型</typeparam>
        /// <returns>写入数据流的位数</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static public int SerializeComponentSingleBaseline<TSerializer>(
            this TSerializer serializer,
            IntPtr snapshot, in IntPtr baseline,
            [NoAlias] IntPtr changeMaskData, ref int startOffset, ref int snapshotOffset,
            ref DataStreamWriter writer, in StreamCompressionModel compressionModel,
            int sendComponent=1)
            where TSerializer : unmanaged, IGhostSerializer
        {
            if(Burst.CompilerServices.Hint.Unlikely(sendComponent == 0))
            {
                if(Burst.CompilerServices.Hint.Likely(serializer.HasGhostFields))
                {
                    var snapshotSize = GhostComponentSerializer.SnapshotSizeAligned(serializer.SizeInSnapshot);
                    GhostComponentSerializer.ClearSnapshotDataAndMask(snapshot, snapshotOffset, snapshotSize,
                        changeMaskData, startOffset, serializer.ChangeMaskSizeInBits);
                    snapshotOffset += snapshotSize;
                    startOffset += serializer.ChangeMaskSizeInBits;
                }
                return 0;
            }
            else
            {
                var currentBits = writer.LengthInBits;
                if (Burst.CompilerServices.Hint.Likely(serializer.HasGhostFields))
                {
                    serializer.SerializeCombined(
                        snapshot + snapshotOffset,
                        baseline + snapshotOffset,
                        changeMaskData, startOffset, ref writer, compressionModel);
                    snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(serializer.SizeInSnapshot);
                    startOffset += serializer.ChangeMaskSizeInBits;
                }
                return writer.LengthInBits - currentBits;
            }
        }

        /// <summary>
        /// 使用三个 Baseline 将指定 Component 序列化到 Data Stream
        /// <see cref="GhostDeltaPredictor"/> 会计算用于 Delta Compression 的新预测 Baseline
        /// </summary>
        /// <param name="serializer">Serializer 实例</param>
        /// <param name="snapshot">Snapshot Buffer 数据</param>
        /// <param name="baseline0">用于计算差值的第一个 Baseline</param>
        /// <param name="baseline1">用于计算差值的第二个 Baseline</param>
        /// <param name="baseline2">用于计算差值的第三个 Baseline</param>
        /// <param name="changeMaskData">ChangeMask 位 Buffer</param>
        /// <param name="startOffset">BitMask 起始 Offset</param>
        /// <param name="snapshotOffset">数据起始 Offset</param>
        /// <param name="predictor">Delta Predictor 实例</param>
        /// <param name="writer">数据写入器</param>
        /// <param name="compressionModel">压缩模型</param>
        /// <param name="sendComponent">是否应复制此 Component</param>
        /// <typeparam name="TSerializer">Serializer 类型</typeparam>
        /// <returns>写入数据流的位数</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static public int SerializeComponentThreeBaseline<TSerializer>(
            this TSerializer serializer,
            IntPtr snapshot, IntPtr baseline0,
            IntPtr baseline1, IntPtr baseline2,
            [NoAlias] IntPtr changeMaskData, ref int startOffset, ref int snapshotOffset,
            ref GhostDeltaPredictor predictor, ref DataStreamWriter writer, in StreamCompressionModel compressionModel,
            int sendComponent=1)
            where TSerializer : unmanaged, IGhostSerializer
        {
            if(Burst.CompilerServices.Hint.Unlikely(sendComponent == 0))
            {
                if(Burst.CompilerServices.Hint.Likely(serializer.HasGhostFields))
                {
                    var snapshotSize = GhostComponentSerializer.SnapshotSizeAligned(serializer.SizeInSnapshot);
                    GhostComponentSerializer.ClearSnapshotDataAndMask(snapshot, snapshotOffset, snapshotSize,
                        changeMaskData, startOffset, serializer.ChangeMaskSizeInBits);
                    snapshotOffset += snapshotSize;
                    startOffset += serializer.ChangeMaskSizeInBits;
                }
                return 0;
            }
            else
            {
                var currentBits = writer.LengthInBits;
                if(Burst.CompilerServices.Hint.Likely(serializer.HasGhostFields))
                {
                    serializer.SerializeWithPredictedBaseline(
                        snapshot + snapshotOffset, baseline0 + snapshotOffset, baseline1 + snapshotOffset,
                        baseline2 + snapshotOffset, ref predictor,
                        changeMaskData, startOffset, ref writer, compressionModel);
                    snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(serializer.SizeInSnapshot);
                    startOffset += serializer.ChangeMaskSizeInBits;
                }
                return writer.LengthInBits - currentBits;
            }
        }

        /// <summary>
        /// 使用默认 Buffer 序列化策略将单个 Buffer 序列化到 Data Stream
        /// </summary>
        /// <param name="serializer">Serializer 实例</param>
        /// <param name="snapshot">Snapshot Buffer 数据</param>
        /// <param name="baseline">用于计算差值的 Baseline，可以是零 Baseline</param>
        /// <param name="snapshotDynamicData">动态 Snapshot Data Buffer</param>
        /// <param name="baselineDynamicData">动态 Snapshot Data Buffer Baseline</param>
        /// <param name="changeMaskData">ChangeMask 位 Buffer</param>
        /// <param name="startOffset">BitMask 起始 Offset</param>
        /// <param name="snapshotOffset">数据起始 Offset</param>
        /// <param name="dynamicSize">动态 Snapshot Buffer 中已写入数据的大小，单位为字节</param>
        /// <param name="writer">数据写入器</param>
        /// <param name="compressionModel">压缩模型</param>
        /// <param name="sendBuffer">是否应发送此 Buffer</param>
        /// <typeparam name="TSerializer">Serializer 类型</typeparam>
        /// <returns>写入数据流的位数</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static public int SerializeBuffer<TSerializer>(
            this TSerializer serializer,
            IntPtr snapshot, IntPtr baseline,
            [NoAlias] IntPtr snapshotDynamicData,
            [NoAlias] IntPtr baselineDynamicData,
            [NoAlias] IntPtr changeMaskData, ref int startOffset, ref int snapshotOffset,
            ref int dynamicSize, ref DataStreamWriter writer, in StreamCompressionModel compressionModel,
            int sendBuffer = 1)
            where TSerializer : unmanaged, IGhostSerializer
        {
            int snapshotSize = serializer.SizeInSnapshot;
            int len = GhostComponentSerializer.TypeCast<int>(snapshot, snapshotOffset);
            int dynamicSnapshotDataOffset = GhostComponentSerializer.TypeCast<int>(snapshot, snapshotOffset + 4);
            var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(serializer.ChangeMaskSizeInBits, len);
            var dataSize = GhostComponentSerializer.SnapshotSizeAligned(maskSize + len * snapshotSize);
            var currentBits = writer.LengthInBits;
            if(Burst.CompilerServices.Hint.Unlikely(sendBuffer == 0))
            {
                GhostComponentSerializer.ResetChangeMask(changeMaskData, startOffset, 2);
                var sizeInSnapshot = GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
                GhostComponentSerializer.ClearSnapshotDataAndMask(snapshot, snapshotOffset, sizeInSnapshot,
                    changeMaskData, startOffset, serializer.ChangeMaskSizeInBits);
                dynamicSize += dataSize;
                snapshotOffset += sizeInSnapshot;
                startOffset += GhostComponentSerializer.DynamicBufferComponentMaskBits;
                return 0;
            }
            else
            {
                DefaultBufferSerialization.SerializeBufferToStream(serializer,
                    baseline, snapshotOffset,
                    changeMaskData, startOffset, serializer.ChangeMaskSizeInBits,
                    snapshotDynamicData, baselineDynamicData,
                    len, dynamicSnapshotDataOffset, snapshotSize, maskSize,
                    ref writer, compressionModel);
                dynamicSize += dataSize;
                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
                startOffset += GhostComponentSerializer.DynamicBufferComponentMaskBits;
            }
            return writer.LengthInBits - currentBits;
        }
    }
}
