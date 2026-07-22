using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using System.Runtime.InteropServices;
namespace Unity.NetCode
{
    /// <summary>
    /// 仅供内部使用
    /// 所有代码生成 ISystem 的接口，这些系统负责将生成的组件 Serializer 注册到
    /// <see cref="GhostComponentSerializerCollectionSystemGroup"/>
    /// </summary>
    public interface IGhostComponentSerializerRegistration
    {}
}

namespace Unity.NetCode.LowLevel.Unsafe
{
    /// <summary>
    /// 主要供内部使用，包含代码生成和部分运行时系统使用的一组辅助函数
    /// 参见 <see cref="GhostSendSystem"/>、<see cref="GhostReceiveSystem"/> 等
    /// 如需处理 Ghost Snapshot，参见 <see cref="SnapshotData"/> 和 <see cref="SnapshotDynamicDataBuffer"/>
    /// 此类型还声明了所有 Ghost 组件和 Buffer Serializer 的委托方法
    /// 用于在运行时将代码生成的 Serializer 注册到 <see cref="GhostComponentSerializer.State"/> 集合
    /// </summary>
    public unsafe struct GhostComponentSerializer
    {
        ///<summary>
        /// Dynamic Buffer 在 Snapshot 数据中具有一个特殊条目，用于跟踪 Buffer 数据在
        /// <see cref="SnapshotDynamicDataBuffer"/> 中的长度和偏移量，此影子组件条目采用以下格式
        /// <list type="bullet">
        /// <item>uint Length：Buffer 长度</item>
        /// <item>uint Offset：相对于该历史槽位 Dynamic Data Buffer 起点的字节位置</item>
        /// </list>
        /// </summary>
        public const int DynamicBufferComponentSnapshotSize = sizeof(uint) + sizeof(uint);
        /// <summary>
        /// 影子 Buffer 数据使用的 ChangeMask 位数，Buffer 的 ChangeMask 格式如下
        /// <list type="bullet">
        /// <item>00：没有变化</item>
        /// <item>01：长度不变，内容发生变化</item>
        /// <item>10：长度发生变化，同时视为内容也发生变化，此规则未来可能改变</item>
        /// </list>
        /// </summary>
        public const int DynamicBufferComponentMaskBits = 2;
        /// <summary>
        /// 用于标记组件应序列化到哪些 Ghost 类型的位标志
        /// </summary>
        /// <remarks>此类型与 <see cref="GhostSendType"/> 重复，应改用后者</remarks>
        [Flags]
        [Obsolete("Due to changes to the source generator, this enum is now both redundant and deprecated, as it duplicates `GhostSendType`. Unfortunately, not UnityUpgradable to GhostSendType as enum names have changed. (RemovedAfter Entities 1.0)", false)]
        public enum SendMask
        {
            /// <summary>
            /// 不应复制该组件
            /// </summary>
            /// <remarks>映射到 <see cref="GhostSendType.DontSend"/></remarks>
            None = 0,
            /// <summary>
            /// 仅向插值 Ghost 复制该组件
            /// </summary>
            /// <remarks>映射到 <see cref="GhostSendType.OnlyInterpolatedClients"/></remarks>
            Interpolated = 1,
            /// <summary>
            /// 仅向预测 Ghost 复制该组件
            /// </summary>
            /// <remarks>映射到 <see cref="GhostSendType.OnlyPredictedClients"/></remarks>
            Predicted = 2,
        }

        /// <summary>
        /// Ghost 使用预序列化优化时，对组件执行后序列化的委托方法
        /// </summary>
        /// <param name="snapshotData">Snapshot 数据</param>
        /// <param name="snapshotOffset">Snapshot 偏移量</param>
        /// <param name="snapshotStride">Snapshot 步长</param>
        /// <param name="maskOffsetInBits">以位为单位的 Mask 偏移量</param>
        /// <param name="count">数量</param>
        /// <param name="baselines">Snapshot 基线</param>
        /// <param name="writer">数据流写入器</param>
        /// <param name="compressionModel">压缩模型</param>
        /// <param name="entityStartBit">实体起始位</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PostSerializeDelegate(IntPtr snapshotData, int snapshotOffset, int snapshotStride, int maskOffsetInBits, int count, IntPtr baselines, ref DataStreamWriter writer, ref StreamCompressionModel compressionModel, IntPtr entityStartBit);
        /// <summary>
        /// Ghost 使用预序列化优化时，对 Buffer 执行后序列化的委托方法
        /// </summary>
        /// <param name="snapshotData">Snapshot 数据</param>
        /// <param name="snapshotOffset">Snapshot 偏移量</param>
        /// <param name="snapshotStride">Snapshot 步长</param>
        /// <param name="maskOffsetInBits">以位为单位的 Mask 偏移量</param>
        /// <param name="changeMaskBits">ChangeMask 位数</param>
        /// <param name="count">数量</param>
        /// <param name="baselines">Snapshot 基线</param>
        /// <param name="writer">数据流写入器</param>
        /// <param name="compressionModel">压缩模型</param>
        /// <param name="entityStartBit">实体起始位</param>
        /// <param name="snapshotDynamicDataPtr">动态数据指针</param>
        /// <param name="dynamicSizePerEntity">每个实体的动态数据大小</param>
        /// <param name="dynamicSnapshotMaxOffset">动态 Snapshot 的最大偏移量</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PostSerializeBufferDelegate(IntPtr snapshotData, int snapshotOffset, int snapshotStride, int maskOffsetInBits, int changeMaskBits, int count, IntPtr baselines, ref DataStreamWriter writer, ref StreamCompressionModel compressionModel, IntPtr entityStartBit, IntPtr snapshotDynamicDataPtr, IntPtr dynamicSizePerEntity, int dynamicSnapshotMaxOffset);
        /// <summary>
        /// 将根实体的组件数据序列化到出站数据流的委托方法
        /// 按批次处理
        /// </summary>
        /// <param name="stateData">状态数据</param>
        /// <param name="snapshotData">Snapshot 数据</param>
        /// <param name="snapshotOffset">Snapshot 偏移量</param>
        /// <param name="snapshotStride">Snapshot 步长</param>
        /// <param name="maskOffsetInBits">以位为单位的 Mask 偏移量</param>
        /// <param name="componentData">组件数据</param>
        /// <param name="count">数量</param>
        /// <param name="baselines">Snapshot 基线</param>
        /// <param name="writer">数据流写入器</param>
        /// <param name="compressionModel">压缩模型</param>
        /// <param name="entityStartBit">实体起始位</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SerializeDelegate(IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride, int maskOffsetInBits, IntPtr componentData, int count, IntPtr baselines, ref DataStreamWriter writer, ref StreamCompressionModel compressionModel, IntPtr entityStartBit);
        /// <summary>
        /// 将子实体的组件数据序列化到出站数据流的委托方法
        /// 每次处理一个实体
        /// </summary>
        /// <param name="stateData">状态数据</param>
        /// <param name="snapshotData">Snapshot 数据</param>
        /// <param name="snapshotOffset">Snapshot 偏移量</param>
        /// <param name="snapshotStride">Snapshot 步长</param>
        /// <param name="maskOffsetInBits">以位为单位的 Mask 偏移量</param>
        /// <param name="componentData">组件数据</param>
        /// <param name="count">数量</param>
        /// <param name="baselines">Snapshot 基线</param>
        /// <param name="writer">数据流写入器</param>
        /// <param name="compressionModel">压缩模型</param>
        /// <param name="entityStartBit">实体起始位</param>
        [Obsolete("The SerializeChildDelegate delegate has been deprecated and will be removed. Please use only use the SerializeDelegate instead", false)]
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SerializeChildDelegate(IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride, int maskOffsetInBits, IntPtr componentData, int count, IntPtr baselines, ref DataStreamWriter writer, ref StreamCompressionModel compressionModel, IntPtr entityStartBit);
        /// <summary>
        /// 序列化整个 Chunk 中 Buffer 内容的委托方法
        /// 按批次处理
        /// </summary>
        /// <param name="stateData">状态数据</param>
        /// <param name="snapshotData">Snapshot 数据</param>
        /// <param name="snapshotOffset">Snapshot 偏移量</param>
        /// <param name="snapshotStride">Snapshot 步长</param>
        /// <param name="maskOffsetInBits">以位为单位的 Mask 偏移量</param>
        /// <param name="changeMaskBits">ChangeMask 位数</param>
        /// <param name="componentData">组件数据</param>
        /// <param name="componentDataLen">组件数据长度</param>
        /// <param name="count">数量</param>
        /// <param name="baselines">Snapshot 基线</param>
        /// <param name="writer">数据流写入器</param>
        /// <param name="compressionModel">压缩模型</param>
        /// <param name="entityStartBit">实体起始位</param>
        /// <param name="snapshotDynamicDataPtr">动态数据指针</param>
        /// <param name="snapshotDynamicDataOffset">动态数据指针偏移量</param>
        /// <param name="dynamicSizePerEntity">每个实体的动态数据大小</param>
        /// <param name="dynamicSnapshotMaxOffset">动态 Snapshot 的最大偏移量</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SerializeBufferDelegate(IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride, int maskOffsetInBits, int changeMaskBits, IntPtr componentData, IntPtr componentDataLen, int count, IntPtr baselines, ref DataStreamWriter writer, ref StreamCompressionModel compressionModel, IntPtr entityStartBit, IntPtr snapshotDynamicDataPtr, ref int snapshotDynamicDataOffset, IntPtr dynamicSizePerEntity, int dynamicSnapshotMaxOffset);
        /// <summary>
        /// 在组件数据与 Snapshot Buffer 之间传输数据的委托方法
        /// </summary>
        /// <param name="stateData">状态数据</param>
        /// <param name="snapshotData">Snapshot 数据</param>
        /// <param name="snapshotOffset">Snapshot 偏移量</param>
        /// <param name="snapshotStride">Snapshot 步长</param>
        /// <param name="componentData">组件数据</param>
        /// <param name="componentStride">组件步长</param>
        /// <param name="count">数量</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void CopyToFromSnapshotDelegate(IntPtr stateData, IntPtr snapshotData, int snapshotOffset, int snapshotStride, IntPtr componentData, int componentStride, int count);
        /// <summary>
        /// 从 <see cref="GhostPredictionHistoryState"/> Buffer 恢复已复制组件状态的委托方法
        /// 由于历史 Buffer 会对完整组件数据执行内存复制，因此必须调用此方法
        /// 以确保实际只恢复组件中参与复制的部分
        /// </summary>
        /// <param name="componentData">组件数据</param>
        /// <param name="backupData">备份数据</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void RestoreFromBackupDelegate(IntPtr componentData, IntPtr backupData);
        /// <summary>
        /// 计算组件和 Buffer 的预测增量，用于增量压缩
        /// </summary>
        /// <param name="snapshotData">Snapshot 数据</param>
        /// <param name="baseline1Data">Snapshot 基线</param>
        /// <param name="baseline2Data">Snapshot 基线</param>
        /// <param name="predictor">增量预测器</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PredictDeltaDelegate(IntPtr snapshotData, IntPtr baseline1Data, IntPtr baseline2Data, ref GhostDeltaPredictor predictor);
        /// <summary>
        /// 从接收的 Snapshot 反序列化组件和 Buffer 数据，并存入 <see cref="SnapshotDataBuffer"/>
        /// </summary>
        /// <param name="snapshotData">Snapshot 数据</param>
        /// <param name="baselineData">Snapshot 基线</param>
        /// <param name="reader">数据流读取器</param>
        /// <param name="compressionModel">压缩模型</param>
        /// <param name="changeMaskData">ChangeMask 数据</param>
        /// <param name="startOffset">起始偏移量</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DeserializeDelegate(IntPtr snapshotData, IntPtr baselineData, ref DataStreamReader reader, ref StreamCompressionModel compressionModel, IntPtr changeMaskData, int startOffset);
        /// <summary>
        /// <see cref="GhostPredictionDebugSystem"/> 使用的委托，用于收集并报告所有复制字段的预测误差
        /// </summary>
        /// <param name="componentData">组件数据</param>
        /// <param name="backupData">备份数据</param>
        /// <param name="errorsList">误差列表</param>
        /// <param name="errorsCount">误差数量</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReportPredictionErrorsDelegate(IntPtr componentData, IntPtr backupData, IntPtr errorsList, int errorsCount);

        /// <summary>
        /// 此 Buffer 添加到 GhostCollection 单例实体
        /// 用于存储 Ghost 的序列化元数据
        /// 由于体积过大而无法存入 Chunk 内存
        /// 其中的值由 Source Generator 生成
        /// </summary>
        [InternalBufferCapacity(0)]
        public struct State : IBufferElementData
        {
            /// <summary>
            /// 由 Source Generator 计算、用于标识 Serializer 类型的唯一 Hash
            /// </summary>
            public ulong SerializerHash;
            /// <summary>
            /// 所有 Serializer 字段及其 <see cref="GhostFieldAttribute"/> 选项属性的 Hash
            /// 用于计算 <see cref="NetworkProtocolVersion"/>
            /// </summary>
            public ulong GhostFieldsHash;
            /// <summary>
            /// 标识此 Serializer 所用特定 Variant 的 Hash，参见 <see cref="GhostComponentVariationAttribute"/>
            /// 未使用 Variant 时，此值为 <see cref="ComponentType"/> 自身的 Hash，且 <see cref="IsDefaultSerializer"/> 为 true
            /// </summary>
            public ulong VariantHash;
            /// <summary>
            /// 此 Serializer 处理的组件类型
            /// </summary>
            public ComponentType ComponentType;
            /// <summary>
            /// 内部字段，指向 <see cref="GhostComponentSerializerCollectionData.SerializationStrategies"/> 列表的索引
            /// </summary>
            public short SerializationStrategyIndex;
            /// <summary>
            /// <see cref="Entities.TypeManager"/> 报告的组件大小
            /// </summary>
            public int ComponentSize;
            /// <summary>
            /// 组件在 Snapshot Buffer 中的大小
            /// </summary>
            public int SnapshotSize;
            /// <summary>
            /// SnapshotSize 是否大于零
            /// </summary>
            public bool HasGhostFields => SnapshotSize > 0;
            /// <summary>
            /// ChangeMask 所需的位数
            /// </summary>
            public int ChangeMaskBits;
            /// <summary>
            /// 此组件具有 <see cref="GhostEnabledBitAttribute"/> 因而应复制启用位标志时为 true
            /// </summary>
            /// <remarks>注意，序列化启用位与主 Serializer 不同，例如空 Variant 也可以拥有序列化启用位</remarks>
            public byte SerializesEnabledBit;
            /// <summary>
            /// 如果组件上存在对应特性，则存储 <see cref="GhostComponentAttribute.PrefabType"/>
            /// 否则设为 <see cref="GhostPrefabType.All"/>
            /// TODO：尝试通过直接读取 ComponentTypeSerializationStrategy 来消除此数据重复
            /// </summary>
            public GhostPrefabType PrefabType;
            /// <summary>
            /// 指示应向哪些类型的 Ghost 复制组件
            /// 此 Mask 由代码生成根据 <see cref="PrefabType"/> 约束设置
            /// </summary>
            public GhostSendType SendMask;
            /// <summary>
            /// 如果组件上存在对应特性，则存储 <see cref="GhostComponentAttribute.OwnerSendType"/>
            /// 否则设为 <see cref="SendToOwnerType.All"/>
            /// </summary>
            public SendToOwnerType SendToOwner;
            /// <summary>
            /// Ghost 使用预序列化优化时，对组件执行后序列化的委托方法
            /// </summary>
            public PortableFunctionPointer<PostSerializeDelegate> PostSerialize;
            /// <summary>
            /// Ghost 使用预序列化优化时，对 Buffer 执行后序列化的委托方法
            /// </summary>
            public PortableFunctionPointer<PostSerializeBufferDelegate> PostSerializeBuffer;
            /// <summary>
            /// 将根实体的组件数据序列化到出站数据流的委托方法，按批次处理
            /// </summary>
            public PortableFunctionPointer<SerializeDelegate> Serialize;
            /// <summary>
            /// 将子实体的组件数据序列化到出站数据流的委托方法
            /// 每次处理一个实体
            /// </summary>
            [Obsolete("The SerializeChild method has been deprecated. Please use only Serialize instead", false)]
            public PortableFunctionPointer<SerializeChildDelegate> SerializeChild;
            /// <summary>
            /// 序列化整个 Chunk 中 Buffer 内容的委托方法，以整个 Chunk 为批次处理
            /// </summary>
            public PortableFunctionPointer<SerializeBufferDelegate> SerializeBuffer;
            /// <summary>
            /// 将组件数据传输到 Snapshot Buffer 的委托方法
            /// </summary>
            public PortableFunctionPointer<CopyToFromSnapshotDelegate> CopyToSnapshot;
            /// <summary>
            /// 将数据从 Snapshot Buffer 传输到目标组件的委托方法
            /// </summary>
            public PortableFunctionPointer<CopyToFromSnapshotDelegate> CopyFromSnapshot;
            /// <summary>
            /// 从 <see cref="GhostPredictionHistoryState"/> Buffer 恢复已复制组件状态的委托方法
            /// 由于历史 Buffer 会对完整组件数据执行内存复制，因此必须调用此方法
            /// 以确保实际只恢复组件中参与复制的部分
            /// </summary>
            public PortableFunctionPointer<RestoreFromBackupDelegate> RestoreFromBackup;
            /// <summary>
            /// 计算组件和 Buffer 的预测增量，用于增量压缩
            /// </summary>
            public PortableFunctionPointer<PredictDeltaDelegate> PredictDelta;
            /// <summary>
            /// 从接收的 Snapshot 反序列化组件和 Buffer 数据，并存入 <see cref="SnapshotDataBuffer"/>
            /// </summary>
            public PortableFunctionPointer<DeserializeDelegate> Deserialize;
            #if UNITY_EDITOR || NETCODE_DEBUG
            /// <summary>
            /// 由 <see cref="GhostPredictionDebugSystem"/> 使用，用于收集并报告所有复制字段的预测误差
            /// </summary>
            public PortableFunctionPointer<ReportPredictionErrorsDelegate> ReportPredictionErrors;
            /// <summary>
            /// 用于分析 Serializer 性能的标记
            /// </summary>
            public Unity.Profiling.ProfilerMarker ProfilerMarker;
            #endif
#if UNITY_EDITOR || NETCODE_DEBUG
            /// <summary>
            /// 包含所有复制字段名称列表的字符串 Buffer
            /// 对于只能进行插值的组件类型，此 Buffer 为空，参见 <see cref="PrefabType"/>
            /// </summary>
            public FixedString512Bytes PredictionErrorNames;
            /// <summary>
            /// <see cref="PredictionErrorNames"/> 列表的长度
            /// </summary>
            internal int NumPredictionErrorNames;
            /// <summary>
            /// <see cref="ReportPredictionErrorsDelegate"/> 方法计算的预测误差数量
            /// 由于名称列表上限为 512 字节，此值可能大于 <see cref="NumPredictionErrorNames"/>
            /// </summary>
            public int NumPredictionErrors;
            /// <summary>
            /// 仅供内部使用，预测误差名称缓存中的索引，参见 <see cref="GhostCollectionSystem"/>
            /// </summary>
            internal int FirstNameIndex;
            /// <summary>
            /// 仅供内部使用，Ghost Variant 类型全名的 Hash，主要用于验证
            /// </summary>
            public ulong VariantTypeFullNameHash;
#endif
        }

        /// <summary>
        /// 返回组件数据在 <see cref="SnapshotData"/> 中所占字节数的辅助方法，结果按 16 字节边界对齐
        /// </summary>
        /// <remarks>
        /// 对于 Buffer，<see cref="SnapshotData"/> 仅包含偏移量和长度信息，Buffer 数据位于
        /// <see cref="SnapshotDynamicDataBuffer"/> 中，返回的大小始终等于 <see cref="GhostComponentSerializer.DynamicBufferComponentSnapshotSize"/>
        /// </remarks>
        /// <param name="serializer">Serializer 状态</param>
        /// <returns>按 16 字节边界对齐的字节数</returns>
        public static int SizeInSnapshot(in State serializer)
        {
            if (!serializer.HasGhostFields)
                return 0;

            return serializer.ComponentType.IsBuffer
                ? SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize)
                : SnapshotSizeAligned(serializer.SnapshotSize);
        }

        /// <summary>
        /// 根据结构体数据的内存地址获取其引用的辅助方法
        /// </summary>
        /// <param name="value">数据</param>
        /// <param name="offset">偏移量</param>
        /// <typeparam name="T">组件类型</typeparam>
        /// <returns>数据中的组件类型引用</returns>
        public static ref T TypeCast<T>(IntPtr value, int offset = 0) where T: struct
        {
            return ref UnsafeUtility.AsRef<T>((byte*)value+offset);
        }
        /// <summary>
        /// 根据结构体数据的内存地址获取其只读引用的辅助方法
        /// </summary>
        /// <param name="value">数据</param>
        /// <param name="offset">偏移量</param>
        /// <typeparam name="T">组件类型</typeparam>
        /// <returns>数据中的组件类型只读引用</returns>
        public static ref readonly T TypeCastReadonly<T>(IntPtr value, int offset = 0) where T: struct
        {
            return ref UnsafeUtility.AsRef<T>((byte*)value+offset);
        }
        /// <summary>
        /// 返回指向给定 <paramref name="value"/> 实例内存地址的指针
        /// </summary>
        /// <param name="value">数据</param>
        /// <typeparam name="T">组件类型</typeparam>
        /// <returns>指向数据中组件类型的指针</returns>
        public static IntPtr IntPtrCast<T>(ref T value) where T: struct
        {
            return (IntPtr)UnsafeUtility.AddressOf(ref value);
        }

        /// <summary>
        /// 相对于给定 <paramref name="baseline"/> 对无符号整数 <paramref name="value"/> 进行增量编码所需的压缩位数
        /// </summary>
        /// <param name="value">要编码的值</param>
        /// <param name="baseline">用于计算增量的 Baseline</param>
        /// <param name="model">使用的压缩模型</param>
        /// <returns>编码该值所需的位数</returns>
        static public int GetDeltaCompressedSizeInBits(uint value, uint baseline, in StreamCompressionModel model)
        {
            int delta = (int)(baseline - value);
            uint zigZagEncoded = (uint)((delta >> 31) ^ (delta << 1));
            return model.GetCompressedSizeInBits(zigZagEncoded);
        }

        /// <summary>
        /// 仅供内部使用，将 <paramref name="src"/> 位掩码中指定数量的位复制到目标 Buffer 的给定 <paramref name="offset"/>
        /// </summary>
        /// <param name="bitData">目标 Buffer</param>
        /// <param name="src">位掩码</param>
        /// <param name="offset">复制目标偏移量</param>
        /// <param name="numBits">要复制的位数</param>
        public static void CopyToChangeMask(IntPtr bitData, uint src, int offset, int numBits)
        {
            Assertions.Assert.IsTrue(offset >= 0);
            Assertions.Assert.IsTrue(numBits >= 0);
            Assertions.Assert.IsTrue(numBits <= 32);
            // 要求 src[31:numBits] 等于 0
            var bits = (uint*)bitData;
            int idx = offset >> 5;
            int bitIdx = offset & 0x1f;
            // 先清除即将写入的位，确保即使原值不为零也能写入正确结果
            bits[idx] &= (uint)(((1UL << bitIdx)-1) | ~((1UL << (bitIdx+numBits))-1));
            // 对齐源数据，使其第一位从指定索引开始，再复制源数据位
            bits[idx] |= src << bitIdx;
            // 检查实际复制的位数，如果源数据仍有未复制的位
            // 将剩余位对齐到下一个 uint 的索引 0 并继续复制
            int usedBits = 32 - bitIdx;
            if (numBits > usedBits && usedBits < 32)
            {
                // 先清除即将写入的位，确保即使原值不为零也能写入正确结果
                bits[idx+1] &= ~((1u << (numBits-usedBits))-1);
                bits[idx+1] |= src >> usedBits;
            }
        }

        /// <summary>
        /// 仅供内部使用，从给定 <paramref name="offset"/> 开始，将 <paramref name="bitData"/> 位掩码中指定数量的位清零
        /// </summary>
        /// <param name="bitData">位掩码</param>
        /// <param name="offset">偏移量</param>
        /// <param name="numBits">位数</param>
        public static void ResetChangeMask(IntPtr bitData, int offset, int numBits)
        {
            Assertions.Assert.IsTrue(offset >= 0);
            Assertions.Assert.IsTrue(numBits >= 0);
            var bits = (uint*)bitData;
            int idx = offset >> 5;
            int bitIdx = offset & 0x1f;
            var remainingBits = 32 - bitIdx;
            // 如果所有位都位于当前 int 内，则直接将对应区域清零
            if (numBits < remainingBits)
            {
                bits[idx] &= (uint)(((1UL << bitIdx)-1) | ~((1UL << (bitIdx+numBits))-1));
            }
            else
            {
                // 清零直到当前 32 位块末尾，使后续处理对齐到下一个块
                bits[idx] &= (uint)(((1UL << bitIdx)-1));
                numBits -= remainingBits;
                // 将所有完整的 Mask 字清零
                while (numBits > 32)
                {
                    bits[++idx] = 0;
                    numBits -=32;
                }
                // 从偏移量 0 开始，清除下一个 ChangeMask uint 中的剩余位
                if (numBits > 0)
                {
                    bits[++idx] &= ~((1u << numBits)-1);
                }
            }
        }

        /// <summary>
        /// 将 ChangeMask 和 Snapshot 数据重置为默认值，即全部清零
        /// </summary>
        /// <param name="snapshot">Snapshot 数据</param>
        /// <param name="snapshotOffset">Snapshot 偏移量</param>
        /// <param name="snapshotSize">Snapshot 大小</param>
        /// <param name="changeMask">ChangeMask 数据</param>
        /// <param name="maskOffset">Mask 偏移量</param>
        /// <param name="changeMaskBits">ChangeMask 位数</param>
        public static void ClearSnapshotDataAndMask(IntPtr snapshot, int snapshotOffset, int snapshotSize, IntPtr changeMask, int maskOffset,
            int changeMaskBits)
        {
            ResetChangeMask(changeMask, maskOffset, changeMaskBits);
            var componentUintSize = SnapshotSizeAligned(snapshotSize)/4;
            var snapshotData = (uint*)(snapshot + snapshotOffset);
            for(int i=0;i<componentUintSize;++i) snapshotData[i] = 0;
        }

        /// <summary>
        /// 仅供内部使用，将位掩码数组给定 <param name="offset">偏移量</param> 处的一位清零
        /// </summary>
        /// <param name="bitData">位掩码数组</param>
        /// <param name="offset">要清零位的偏移量</param>
        static internal void ResetChangeMaskBit(IntPtr bitData, int offset)
        {
            Assertions.Assert.IsTrue(offset >= 0);
            var bits = (uint*)bitData;
            int idx = offset >> 5;
            int bitIdx = offset & 0x1f;
            bits[idx] &= ~(1U << bitIdx);
        }

        /// <summary>
        /// 从源 Buffer 提取一个无符号整数，表示从给定偏移量开始的部分位掩码
        /// 提取指定数量的位，最多 32 位
        /// </summary>
        /// <param name="bitData">位掩码数组</param>
        /// <param name="offset">提取整数的偏移量</param>
        /// <param name="numBits">要提取的位数</param>
        /// <returns>提取出的无符号整数</returns>
        public static uint CopyFromChangeMask(IntPtr bitData, int offset, int numBits)
        {
            Assertions.Assert.IsTrue(offset >= 0);
            Assertions.Assert.IsTrue(numBits >= 0);
            var bits = (uint*)bitData;
            int idx = offset >> 5;
            int bitIdx = offset & 0x1f;
            // 对齐数据，使大数组的第一位从复制后位掩码的索引 0 开始
            uint result = bits[idx] >> bitIdx;
            // 检查实际复制的位数，如果源数据仍有未复制的位
            // 将剩余位对齐到下一个 uint 的索引 0 并继续复制
            int usedBits = 32 - bitIdx;
            if (numBits > usedBits && usedBits < 32)
                result |= bits[idx+1] << usedBits;
            return result;
        }

        /// <summary>
        /// 根据给定 IntPtr 和长度构造 <see cref="UnsafeList{T}"/> 的辅助方法
        /// </summary>
        /// <param name="floatData">浮点数数据</param>
        /// <param name="len">要转换的浮点数数量</param>
        /// <returns>转换后的浮点数列表</returns>
        public static UnsafeList<float> ConvertToUnsafeList(IntPtr floatData, int len)
        {
            return new UnsafeList<float>((float*)floatData.ToPointer(), len);
        }

        internal static int SnapshotHeaderSizeInBytes(in GhostCollectionPrefabSerializer prefabSerializer)
        {
            return SnapshotSizeAligned(sizeof(uint) + ChangeMaskArraySizeInBytes(prefabSerializer.ChangeMaskBits) + ChangeMaskArraySizeInBytes(prefabSerializer.EnableableBits));
        }

        /// <summary>
        /// 计算编码指定位数所需的 uint 数量
        /// </summary>
        /// <param name="numBits">要编码的位数</param>
        /// <returns>编码这些位所需的 uint 数量</returns>
        public static int ChangeMaskArraySizeInUInts(int numBits)
        {
            return (numBits + 31)>>5;
        }

        /// <summary>
        /// 计算编码指定位数所需的字节数
        /// </summary>
        /// <param name="numBits">要编码的位数</param>
        /// <returns>存储这些位所需的最小字节数，为数据对齐向上取整到 4 字节</returns>
        public static int ChangeMaskArraySizeInBytes(int numBits)
        {
            return ((numBits + 31)>>3) & ~0x3;
        }

        /// <summary>
        /// 将给定大小对齐到 16 字节边界
        /// </summary>
        /// <param name="size">要对齐的大小</param>
        /// <returns>按 16 字节对齐后的新大小</returns>
        public static int SnapshotSizeAligned(int size)
        {
            // TODO：这里可以使用 CollectionHelper.Align
            return (size + 15) & (~15);
        }

        /// <summary>
        /// 将给定大小对齐到 16 字节边界
        /// </summary>
        /// <param name="size">要对齐的大小</param>
        /// <returns>按 16 字节对齐后的新大小</returns>
        public static uint SnapshotSizeAligned(uint size)
        {
            return (size + 15u) & (~15u);
        }

        /// <summary>
        /// 仅供内部使用，主要用于代码生成，重置压缩位流中为每个实体记录的起始和结束位置
        /// </summary>
        /// <param name="count">要重置的 entityStartBits 数组长度</param>
        /// <param name="writer">输出流</param>
        /// <param name="entityStartBit">要重置的起止偏移量对数组</param>
        public static unsafe void ResetEntityStartBits(int count, ref DataStreamWriter writer, IntPtr entityStartBit)
        {
            int* startBitIntPtr = (int*)entityStartBit;
            for (int i = 0; i < count; ++i)
            {
                startBitIntPtr[2 * i] = writer.Length / sizeof(int);
                startBitIntPtr[2 * i + 1] = 0;
            }
        }
    }

    internal static class DynamicBufferExtensions
    {
        /// <summary>
        /// 获取给定索引处元素的只读引用
        /// </summary>
        /// <param name="buffer">元素 Buffer</param>
        /// <param name="index">元素索引</param>
        /// <typeparam name="T">元素类型</typeparam>
        /// <returns>元素的只读引用</returns>
        public static ref readonly T ElementAtRO<T>(this DynamicBuffer<T> buffer, int index) where T: unmanaged, IBufferElementData
        {
            unsafe
            {
                var ptr = (T*)buffer.GetUnsafeReadOnlyPtr();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if(index < 0 || index >= buffer.Length)
                    throw new IndexOutOfRangeException($"Index {index} is out of range in DynamicBuffer of '{buffer.Length}' Length.");
#endif
                return ref ptr[index];
            }
        }
    }
    /// <summary>
    /// 供代码生成使用的辅助类，用于以 Span 形式访问固定大小容器
    /// </summary>
    public static unsafe class FixedArraySerializationUtils
    {
        /// <summary>
        /// 在普通非托管类型引用的部分内存上创建新的 <see cref="Span"/>
        /// </summary>
        /// <param name="container">容器元素引用</param>
        /// <param name="length">元素数量</param>
        /// <typeparam name="TContainer">非托管容器类型</typeparam>
        /// <typeparam name="TElement">非托管元素类型</typeparam>
        /// <returns>从容器引用地址开始、具有给定长度的新 <see cref="Span"/></returns>
        public static unsafe Span<TElement> ToSpan<TContainer, TElement>(ref TContainer container, int length)
            where TContainer: unmanaged
            where TElement: unmanaged
        {
            fixed(void *ptr = &container)
            {
                return new Span<TElement>(ptr, length);
            }
        }
        /// <summary>
        /// 在普通非托管类型引用的部分内存上创建新的 <see cref="ReadOnlySpan"/>
        /// </summary>
        /// <param name="container">容器元素引用</param>
        /// <param name="length">元素数量</param>
        /// <typeparam name="TContainer">非托管容器类型</typeparam>
        /// <typeparam name="TElement">非托管元素类型</typeparam>
        /// <returns>从容器引用地址开始、具有给定长度的新 <see cref="ReadOnlySpan"/></returns>
        public static ReadOnlySpan<TElement> ToReadOnlySpan<TContainer, TElement>(ref TContainer container, int length)
            where TContainer: unmanaged
            where TElement: unmanaged
        {
            fixed(void *ptr = &container)
            {
                return new ReadOnlySpan<TElement>(ptr, length);
            }
        }

        /// <summary>
        /// 在普通非托管 FixedList 引用的部分内存上创建新的 <see cref="Span"/>
        /// </summary>
        /// <remarks>请谨慎使用，因为内部调用了
        /// <see cref="System.Runtime.InteropServices.MemoryMarshal.CreateSpan"/>
        /// </remarks>
        /// <param name="container">FixedList 引用</param>
        /// <param name="length">元素数量</param>
        /// <typeparam name="TElement">非托管参数类型</typeparam>
        /// <returns>从容器引用地址开始、具有给定长度的新 <see cref="Span"/></returns>
        public static Span<TElement> ToSpan<TElement>(ref this FixedList32Bytes<TElement> container, int length)
            where TElement: unmanaged
        {
            return MemoryMarshal.CreateSpan(ref container.ElementAt(0), length);
        }
        /// <inheritdoc cref="ToReadOnlySpan{TELement}"/>
        public static Span<TElement> ToSpan<TElement>(ref this FixedList64Bytes<TElement> container, int length)
            where TElement: unmanaged
        {
            return MemoryMarshal.CreateSpan(ref container.ElementAt(0), length);
        }
        /// <inheritdoc cref="ToReadOnlySpan"/>
        public static Span<TElement> ToSpan<TElement>(ref this FixedList128Bytes<TElement> container, int length)
            where TElement: unmanaged
        {
            return MemoryMarshal.CreateSpan(ref container.ElementAt(0), length);
        }
        /// <inheritdoc cref="ToReadOnlySpan"/>
        public static Span<TElement> ToSpan<TElement>(ref this FixedList512Bytes<TElement> container, int length)
            where TElement: unmanaged
        {
            return MemoryMarshal.CreateSpan(ref container.ElementAt(0), length);
        }
        /// <inheritdoc cref="ToReadOnlySpan"/>
        public static Span<TElement> ToSpan<TElement>(ref this FixedList4096Bytes<TElement> container, int length)
            where TElement: unmanaged
        {
            return MemoryMarshal.CreateSpan(ref container.ElementAt(0), length);
        }

        /// <summary>
        /// 在普通非托管 FixedList 引用的部分内存上创建新的 <see cref="ReadOnlySpan"/>
        /// </summary>
        /// <remarks>请谨慎使用，因为内部调用了
        /// <see cref="System.Runtime.InteropServices.MemoryMarshal.CreateSpan"/>
        /// </remarks>
        /// <param name="container">FixedList 引用</param>
        /// <param name="length">元素数量</param>
        /// <typeparam name="TElement">非托管参数类型</typeparam>
        /// <returns>从容器引用地址开始、具有给定长度的新 <see cref="ReadOnlySpan"/></returns>
        public static ReadOnlySpan<TElement> ToReadOnlySpan<TElement>(ref this FixedList32Bytes<TElement> container, int length)
            where TElement: unmanaged
        {
            return MemoryMarshal.CreateReadOnlySpan(ref container.ElementAt(0), length);
        }
        /// <inheritdoc cref="ToReadOnlySpan"/>
        public static ReadOnlySpan<TElement> ToReadOnlySpan<TElement>(ref this FixedList64Bytes<TElement> container, int length)
            where TElement: unmanaged
        {
            return MemoryMarshal.CreateReadOnlySpan(ref container.ElementAt(0), length);
        }
        /// <inheritdoc cref="ToReadOnlySpan"/>
        public static ReadOnlySpan<TElement> ToReadOnlySpan<TElement>(ref this FixedList128Bytes<TElement> container, int length)
            where TElement: unmanaged
        {
            return MemoryMarshal.CreateReadOnlySpan(ref container.ElementAt(0), length);
        }
        /// <inheritdoc cref="ToReadOnlySpan"/>
        public static ReadOnlySpan<TElement> ToReadOnlySpan<TElement>(ref this FixedList512Bytes<TElement> container, int length)
            where TElement: unmanaged
        {
            return MemoryMarshal.CreateReadOnlySpan(ref container.ElementAt(0), length);
        }
        /// <inheritdoc cref="ToReadOnlySpan"/>
        public static ReadOnlySpan<TElement> ToReadOnlySpan<TElement>(ref this FixedList4096Bytes<TElement> container, int length)
            where TElement: unmanaged
        {
            return MemoryMarshal.CreateReadOnlySpan(ref container.ElementAt(0), length);
        }
    }
}
