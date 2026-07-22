using System;
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
    /// <typeparam name="TComponentType">此辅助类要序列化的 Unmanaged Component</typeparam>
    /// <typeparam name="TSnapshot">包含 Component 数据的 Snapshot Data 结构体</typeparam>
    /// <typeparam name="TSerializer">实现 <see cref="IGhostSerializer"/> 接口的具体类型</typeparam>
    public static unsafe class ComponentSerializationHelper<TComponentType, TSnapshot, TSerializer>
        where TComponentType : unmanaged
        where TSnapshot : unmanaged
        where TSerializer : unmanaged, IGhostSerializer
    {
        const int IntSize = 4;
        const int BaselinesPerEntity = 4;
        private static void SerializeEntities(TSerializer serializer, IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            int maskOffsetInBits, int count, IntPtr baselines, ref DataStreamWriter writer,
            in StreamCompressionModel compressionModel, IntPtr entityStartBit)
        {
            var PtrSize = UnsafeUtility.SizeOf<IntPtr>();
            for (int ent = 0; ent < count; ++ent)
            {
                ref var startuint = ref GhostComponentSerializer.TypeCast<int>(entityStartBit, IntSize * 2 * ent);
                startuint = writer.Length / IntSize;

                // 计算 Baseline
                TSnapshot baseline = default;
                var baseline0Ptr = GhostComponentSerializer.TypeCast<IntPtr>(baselines, PtrSize * ent * BaselinesPerEntity);
                if (baseline0Ptr != IntPtr.Zero)
                {
                    baseline = GhostComponentSerializer.TypeCast<TSnapshot>(baseline0Ptr, snapshotOffset);
                    var baseline2Ptr = GhostComponentSerializer.TypeCast<IntPtr>(baselines, PtrSize * (ent * BaselinesPerEntity + 2));
                    if (baseline2Ptr != IntPtr.Zero)
                    {
                        var baseline1Ptr = GhostComponentSerializer.TypeCast<IntPtr>(baselines, PtrSize * (ent * BaselinesPerEntity + 1));
                        var predictor = new GhostDeltaPredictor(
                            new NetworkTick { SerializedData = GhostComponentSerializer.TypeCast<uint>(snapshotData + snapshotStride * ent) },
                            new NetworkTick { SerializedData = GhostComponentSerializer.TypeCast<uint>(baseline0Ptr) },
                            new NetworkTick { SerializedData = GhostComponentSerializer.TypeCast<uint>(baseline1Ptr) },
                            new NetworkTick { SerializedData = GhostComponentSerializer.TypeCast<uint>(baseline2Ptr) });
                        serializer.PredictDelta(GhostComponentSerializer.IntPtrCast(ref baseline), baseline1Ptr + snapshotOffset,
                            baseline2Ptr + snapshotOffset, ref predictor);
                    }
                }

                var snapshotPtr = snapshotData + snapshotOffset + snapshotStride * ent;
                var baselinePtr = GhostComponentSerializer.IntPtrCast(ref baseline);
                serializer.CalculateChangeMask(snapshotPtr, baselinePtr, snapshotData + IntSize + snapshotStride * ent, maskOffsetInBits);
                serializer.Serialize(snapshotPtr, baselinePtr, snapshotData + IntSize + snapshotStride * ent, maskOffsetInBits, ref writer, compressionModel);
                ref var sbit = ref GhostComponentSerializer.TypeCast<int>(entityStartBit, IntSize * 2 * ent + IntSize);
                sbit = writer.LengthInBits - startuint * 32;
                var missing = 32 - writer.LengthInBits & 31;
                if (missing < 32)
                    writer.WriteRawBits(0, missing);
            }
        }

        /// <summary>
        /// 供 Source Generator 内部使用，将预序列化的 Component Data 写入 <paramref name="writer"/> 数据流
        /// </summary>
        /// <param name="serializer">用于序列化 Component 内容的 IGhostSerialized 实例</param>
        /// <param name="snapshotData">Snapshot Buffer 数据</param>
        /// <param name="snapshotOffset">Snapshot Buffer 中的当前 Offset</param>
        /// <param name="snapshotStride">应用于每个 Entity 的 Stride</param>
        /// <param name="maskOffsetInBits">ChangeMask 位数组中的 Offset</param>
        /// <param name="count">Entity 数量</param>
        /// <param name="baselines">每个 Entity 的 Baseline</param>
        /// <param name="writer">输出 Data Stream</param>
        /// <param name="compressionModel">用于压缩数据流的 Compression Model</param>
        /// <param name="entityStartBit">Data Stream 中起止 Offset 的数组，表示每个 Component 的压缩数据存储位置</param>
        public static void PostSerializeComponents(TSerializer serializer,
            IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            int maskOffsetInBits,
            int count, IntPtr baselines, ref DataStreamWriter writer,
            ref StreamCompressionModel compressionModel,
            IntPtr entityStartBit)
        {
            SerializeEntities(serializer,snapshotData, snapshotOffset, snapshotStride, maskOffsetInBits, count, baselines,
                ref writer, compressionModel, entityStartBit);
        }

        /// <summary>
        /// 供 Source Generator 内部使用，将 Component Data 复制到 Snapshot，计算 ChangeMask，
        /// 并将经过 Delta Compression 的 Snapshot Data 写入 <paramref name="writer"/> 数据流
        /// </summary>
        /// <param name="serializer">用于序列化 Component 内容的 IGhostSerialized 实例</param>
        /// <param name="stateData">指向 <see cref="GhostSerializerState"/> 结构体的指针</param>
        /// <param name="snapshotData">Snapshot Buffer 数据</param>
        /// <param name="snapshotOffset">Snapshot Buffer 中的当前 Offset</param>
        /// <param name="snapshotStride">应用于每个 Entity 的 Stride</param>
        /// <param name="maskOffsetInBits">ChangeMask 位数组中的 Offset</param>
        /// <param name="componentData">指向 Chunk Component Data 的指针</param>
        /// <param name="count">Entity 数量</param>
        /// <param name="baselines">每个 Entity 的 Baseline</param>
        /// <param name="writer">输出 Data Stream</param>
        /// <param name="compressionModel">用于压缩数据流的 Compression Model</param>
        /// <param name="entityStartBit">Data Stream 中起止 Offset 的数组，表示每个 Component 的压缩数据存储位置</param>
        public static void SerializeComponents(TSerializer serializer,
            IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            int maskOffsetInBits, IntPtr componentData, int count, IntPtr baselines, ref DataStreamWriter writer,
            StreamCompressionModel compressionModel, IntPtr entityStartBit)
        {
            ref var serializerState = ref GhostComponentSerializer.TypeCast<GhostSerializerState>(stateData);
            var IntPtrSize = UnsafeUtility.SizeOf<IntPtr>();
            for (int ent = 0; ent < count; ++ent)
            {
                IntPtr curCompData = GhostComponentSerializer.TypeCast<IntPtr>(componentData, IntPtrSize * ent);
                var snapshot = snapshotData + snapshotOffset + snapshotStride * ent;
                if (curCompData != IntPtr.Zero)
                {
                    serializer.CopyToSnapshot(serializerState, snapshot, curCompData);
                }
                else
                {
                    *(TSnapshot*)snapshot = default;
                }
            }

            SerializeEntities(serializer,snapshotData, snapshotOffset, snapshotStride, maskOffsetInBits, count, baselines,
                ref writer, compressionModel, entityStartBit);
        }

        /// <summary>
        /// 使用 <paramref name="serializer"/> 策略将 Component Data 复制到 Snapshot Buffer
        /// </summary>
        /// <param name="stateData">指向 <see cref="GhostSerializerState"/> 结构体的指针</param>
        /// <param name="snapshotData">Snapshot Buffer 数据</param>
        /// <param name="snapshotOffset">Snapshot Buffer 中的当前 Offset</param>
        /// <param name="snapshotStride">每个 Entity 应用于 Snapshot 指针的 Stride</param>
        /// <param name="componentData">指向 Chunk Component Data 的指针</param>
        /// <param name="componentStride">每个 Entity 应用于 Component 指针的 Stride</param>
        /// <param name="count">Entity 数量</param>
        /// <param name="serializer">用于序列化 Component 内容的 IGhostSerialized 实例</param>
        public static void CopyComponentsToSnapshot(IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            IntPtr componentData, int componentStride, int count, TSerializer serializer)
        {
            ref var serializerState = ref GhostComponentSerializer.TypeCast<GhostSerializerState>(stateData);
            for (int i = 0; i < count; ++i)
            {
                var snapshot = snapshotData + snapshotOffset + snapshotStride * i;
                var component = componentData + componentStride * i;
                serializer.CopyToSnapshot(serializerState, snapshot, component);
            }
        }

        /// <summary>
        /// 使用 <paramref name="serializer"/> 策略从 Snapshot Buffer 复制 Component Data
        /// </summary>
        /// <param name="stateData">指向 <see cref="GhostSerializerState"/> 结构体的指针</param>
        /// <param name="snapshotData">Snapshot Buffer 数据</param>
        /// <param name="snapshotOffset">Snapshot Buffer 中的当前 Offset</param>
        /// <param name="snapshotStride">每个 Entity 应用于 Snapshot 指针的 Stride</param>
        /// <param name="componentData">指向 Chunk Component Data 的指针</param>
        /// <param name="componentStride">每个 Entity 应用于 Component 指针的 Stride</param>
        /// <param name="count">Entity 数量</param>
        /// <param name="serializer">用于序列化 Component 内容的 IGhostSerialized 实例</param>
        public static void CopyComponentsFromSnapshot(IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride,
            IntPtr componentData, int componentStride, int count, TSerializer serializer)
        {
            var deserializerState = GhostComponentSerializer.TypeCast<GhostDeserializerState>(stateData);
            for (int i = 0; i < count; ++i)
            {
                ref var snapshotInterpolationData = ref GhostComponentSerializer.TypeCast<SnapshotData.DataAtTick>(snapshotData, snapshotStride * i);
                // 从当前 Tick 数据获取 Ghost Owner ID，并据此计算 Component 与 Buffer 所需的 Owner Mask
                if((deserializerState.SendToOwner & snapshotInterpolationData.RequiredOwnerSendMask) == 0)
                    continue;

                deserializerState.SnapshotTick = snapshotInterpolationData.Tick;
                var snapshotBefore = snapshotInterpolationData.SnapshotBefore + snapshotOffset;
                var snapshotAfter = snapshotInterpolationData.SnapshotAfter + snapshotOffset;
                serializer.CopyFromSnapshot(deserializerState, componentData + componentStride * i,
                    snapshotInterpolationData.InterpolationFactor,
                    snapshotInterpolationData.InterpolationFactor, snapshotBefore, snapshotAfter);
            }
        }
    }
}
