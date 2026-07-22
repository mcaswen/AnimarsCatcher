using System;
using Unity.Burst;
using Unity.Collections;
using UnityEngine.Scripting;

namespace Unity.NetCode
{
    /// <summary>
    /// 公开底层不安全接口，用于将 Component 的全部 Ghost Field 复制到 Snapshot Buffer
    /// 主要供代码生成内部使用，用户代码不应直接使用或实现
    /// </summary>
    public interface IGhostSerializer
    {
        /// <summary>
        /// ChangeMask 所需的位数
        /// </summary>
        public int ChangeMaskSizeInBits { get; }

        /// <summary>
        /// 序列化 Component 包含序列化字段时为 true
        /// </summary>
        public bool HasGhostFields { get; }

        /// <summary>
        /// 序列化数据在 Snapshot Buffer 中的大小
        /// </summary>
        public int SizeInSnapshot { get; }

        /// <summary>
        /// 将 Component Data 复制或转换到 Snapshot
        /// </summary>
        /// <param name="serializerState">Serializer 状态</param>
        /// <param name="snapshot">Snapshot 指针</param>
        /// <param name="component">组件数据</param>
        void CopyToSnapshot(in GhostSerializerState serializerState, [NoAlias]IntPtr snapshot, [ReadOnly][NoAlias]IntPtr component);

        /// <summary>
        /// 将 Snapshot 复制或转换到 Component，并在需要时执行插值
        /// </summary>
        /// <param name="serializerState">Serializer 状态</param>
        /// <param name="component">组件数据</param>
        /// <param name="snapshotInterpolationFactor">插值系数</param>
        /// <param name="snapshotInterpolationFactorRaw">原始插值系数</param>
        /// <param name="snapshotBefore">插值前 Snapshot</param>
        /// <param name="snapshotAfter">插值后 Snapshot</param>
        public void CopyFromSnapshot(in GhostDeserializerState serializerState, [NoAlias] IntPtr component,
            float snapshotInterpolationFactor, float snapshotInterpolationFactorRaw,
            [NoAlias] [ReadOnly] IntPtr snapshotBefore, [NoAlias] [ReadOnly] IntPtr snapshotAfter);

        /// <summary>
        /// 相对指定 Baseline 计算 Snapshot 的 ChangeMask
        /// </summary>
        /// <param name="snapshot">Snapshot 指针</param>
        /// <param name="baseline">Snapshot 基线</param>
        /// <param name="changeMaskData">ChangeMask 数据</param>
        /// <param name="startOffset">起始 Offset</param>
        void CalculateChangeMask([NoAlias][ReadOnly]IntPtr snapshot, [NoAlias][ReadOnly]IntPtr baseline, [NoAlias]IntPtr changeMaskData, int startOffset);

        /// <summary>
        /// 将 Snapshot Data 序列化到 <paramref name="writer"/> 并计算当前 ChangeMask
        /// </summary>
        /// <param name="snapshot">Snapshot 指针</param>
        /// <param name="baseline">Snapshot 基线</param>
        /// <param name="changeMaskData">ChangeMask 数据</param>
        /// <param name="startOffset">起始 Offset</param>
        /// <param name="writer">数据流写入器</param>
        /// <param name="compressionModel">压缩模型</param>
        void SerializeCombined([ReadOnly][NoAlias] IntPtr snapshot, [ReadOnly][NoAlias] IntPtr baseline,
            [NoAlias][ReadOnly]IntPtr changeMaskData, int startOffset,
            ref DataStreamWriter writer, in StreamCompressionModel compressionModel);

        /// <summary>
        /// 将 Snapshot Data 序列化到 <paramref name="writer"/> 并计算当前 ChangeMask
        /// </summary>
        /// <param name="snapshot">Snapshot 指针</param>
        /// <param name="baseline0">Snapshot 基线</param>
        /// <param name="baseline1">Snapshot 基线</param>
        /// <param name="baseline2">Snapshot 基线</param>
        /// <param name="predictor">Delta 预测器</param>
        /// <param name="changeMaskData">ChangeMask 数据</param>
        /// <param name="startOffset">起始 Offset</param>
        /// <param name="writer">数据流写入器</param>
        /// <param name="compressionModel">压缩模型</param>
        void SerializeWithPredictedBaseline([ReadOnly] [NoAlias] IntPtr snapshot,
            [ReadOnly] [NoAlias] IntPtr baseline0,
            [ReadOnly] [NoAlias] IntPtr baseline1,
            [ReadOnly] [NoAlias] IntPtr baseline2,
            ref GhostDeltaPredictor predictor,
            [NoAlias] [ReadOnly] IntPtr changeMaskData, int startOffset,
            ref DataStreamWriter writer, in StreamCompressionModel compressionModel);

        /// <summary>
        /// 根据已计算的 ChangeMask 将 Snapshot Data 序列化到 <paramref name="writer"/>
        /// 要求 ChangeMask 位已经全部设置
        /// </summary>
        /// <param name="snapshot">Snapshot 指针</param>
        /// <param name="baseline">Snapshot 基线</param>
        /// <param name="changeMaskData">ChangeMask 数据</param>
        /// <param name="startOffset">起始 Offset</param>
        /// <param name="writer">数据流写入器</param>
        /// <param name="compressionModel">压缩模型</param>
        void Serialize([ReadOnly][NoAlias] IntPtr snapshot, [ReadOnly][NoAlias] IntPtr baseline,
            [NoAlias][ReadOnly]IntPtr changeMaskData, int startOffset,
            ref DataStreamWriter writer, in StreamCompressionModel compressionModel);

        /// <summary>
        /// 根据两个 Baseline 计算预测 Snapshot
        /// </summary>
        /// <param name="snapshotData">预测 Snapshot Data</param>
        /// <param name="baseline1Data">Snapshot 基线</param>
        /// <param name="baseline2Data">Snapshot 基线</param>
        /// <param name="predictor">Delta 预测器</param>
        void PredictDelta([NoAlias] IntPtr snapshotData, [NoAlias] IntPtr baseline1Data, [NoAlias] IntPtr baseline2Data, ref GhostDeltaPredictor predictor);

        /// <summary>
        /// 从 <paramref name="reader"/> 数据流读取数据并写入 Snapshot Data
        /// </summary>
        /// <param name="reader">数据流读取器</param>
        /// <param name="compressionModel">压缩模型</param>
        /// <param name="changeMask">ChangeMask 数据</param>
        /// <param name="startOffset">起始 Offset</param>
        /// <param name="snapshot">Snapshot 指针</param>
        /// <param name="baseline">Snapshot 基线</param>
        void Deserialize(ref DataStreamReader reader, in StreamCompressionModel compressionModel,
            IntPtr changeMask,
            int startOffset, [NoAlias]IntPtr snapshot, [NoAlias][ReadOnly]IntPtr baseline);

        /// <summary>
        /// 从预测备份缓冲区恢复 Component Data，仅恢复已序列化字段
        /// </summary>
        /// <param name="component">组件数据</param>
        /// <param name="backup">备份缓冲区</param>
        void RestoreFromBackup([NoAlias]IntPtr component, [NoAlias][ReadOnly]IntPtr backup);

#if UNITY_EDITOR || NETCODE_DEBUG
        /// <summary>
        /// 计算此 Component 的预测误差
        /// </summary>
        /// <param name="component">组件数据</param>
        /// <param name="backup">备份缓冲区</param>
        /// <param name="errorsList">错误列表指针</param>
        /// <param name="errorsCount">错误数量</param>
        void ReportPredictionErrors([NoAlias][ReadOnly]IntPtr component, [NoAlias][ReadOnly]IntPtr backup, IntPtr errorsList,
            int errorsCount);
#endif
    }

    /// <summary>
    /// 所有 Component 和 Buffer Serializer 实现的接口，仅供内部使用
    /// </summary>
    /// <typeparam name="TSnapshot">包含 Component Data 的 Snapshot 结构体类型</typeparam>
    /// <typeparam name="TComponent">此接口要序列化的 Component 类型</typeparam>
    [RequireImplementors]
    [Obsolete("The IGhostSerializer<TComponent, TSnapshot> has been deprecated. Please use the IGhostComponentSerializer instead")]
    public interface IGhostSerializer<TComponent, TSnapshot>
        where TSnapshot: unmanaged
        where TComponent: unmanaged
    {
        /// <summary>
        /// 计算预测 Baseline
        /// </summary>
        /// <param name="snapshot">Snapshot 引用</param>
        /// <param name="baseline1">Snapshot 基线</param>
        /// <param name="baseline2">Snapshot 基线</param>
        /// <param name="predictor">Delta 预测器</param>
        void PredictDeltaGenerated(ref TSnapshot snapshot, in TSnapshot baseline1, in TSnapshot baseline2, ref GhostDeltaPredictor predictor);

        /// <summary>
        /// 相对指定 Baseline 计算 Snapshot 的 ChangeMask
        /// </summary>
        /// <param name="snapshot">Snapshot 引用</param>
        /// <param name="baseline">Snapshot 基线</param>
        /// <param name="changeMaskData">ChangeMask 数据</param>
        /// <param name="startOffset">起始 Offset</param>
        void CalculateChangeMaskGenerated(in TSnapshot snapshot, in TSnapshot baseline, IntPtr changeMaskData, int startOffset){}

        /// <summary>
        /// 将 Snapshot 中的数据复制或转换到 Component，支持插值和外推
        /// </summary>
        /// <param name="serializerState">Serializer 状态</param>
        /// <param name="component">组件数据</param>
        /// <param name="interpolationFactor">插值系数</param>
        /// <param name="snapshotInterpolationFactorRaw">Snapshot 原始插值系数</param>
        /// <param name="snapshotBefore">插值前 Snapshot</param>
        /// <param name="snapshotAfter">插值后 Snapshot</param>
        void CopyFromSnapshotGenerated(in GhostDeserializerState serializerState, ref TComponent component,
            float interpolationFactor, float snapshotInterpolationFactorRaw, in TSnapshot snapshotBefore,
            in TSnapshot snapshotAfter);

        /// <summary>
        /// 将 Component Data 复制或转换到 Snapshot
        /// </summary>
        /// <param name="serializerState">Serializer 状态</param>
        /// <param name="snapshot">Snapshot 引用</param>
        /// <param name="component">组件数据</param>
        void CopyToSnapshotGenerated(in GhostSerializerState serializerState, ref TSnapshot snapshot,
            in TComponent component);

        /// <summary>
        /// 根据已计算的 ChangeMask 将 Snapshot Data 序列化到 <paramref name="writer"/>
        /// </summary>
        /// <param name="snapshot">Snapshot 引用</param>
        /// <param name="baseline">Snapshot 基线</param>
        /// <param name="changeMaskData">ChangeMask 数据</param>
        /// <param name="startOffset">起始 Offset</param>
        /// <param name="writer">数据流写入器</param>
        /// <param name="compressionModel">压缩模型</param>
        void SerializeGenerated(in TSnapshot snapshot, in TSnapshot baseline,
            [ReadOnly][NoAlias]IntPtr changeMaskData, int startOffset, ref DataStreamWriter writer,
            in StreamCompressionModel compressionModel);

        /// <summary>
        /// 根据已计算的 ChangeMask 将 Snapshot Data 序列化到 <paramref name="writer"/>
        /// </summary>
        /// <param name="snapshot">Snapshot 引用</param>
        /// <param name="baseline">Snapshot 基线</param>
        /// <param name="changeMaskData">ChangeMask 数据</param>
        /// <param name="startOffset">起始 Offset</param>
        /// <param name="writer">数据流写入器</param>
        /// <param name="compressionModel">压缩模型</param>
        void SerializeCombinedGenerated(in TSnapshot snapshot, in TSnapshot baseline,
            [NoAlias][ReadOnly]IntPtr changeMaskData, int startOffset,
            ref DataStreamWriter writer, in StreamCompressionModel compressionModel);

        /// <summary>
        /// 从 <paramref name="reader"/> 数据流读取数据并写入 Snapshot Data
        /// </summary>
        /// <param name="reader">数据流读取器</param>
        /// <param name="compressionModel">压缩模型</param>
        /// <param name="changeMask">ChangeMask 数据</param>
        /// <param name="startOffset">起始 Offset</param>
        /// <param name="snapshot">Snapshot 引用</param>
        /// <param name="baseline">Snapshot 基线</param>
        void DeserializeGenerated(ref DataStreamReader reader, in StreamCompressionModel compressionModel,
            IntPtr changeMask,
            int startOffset, ref TSnapshot snapshot, in TSnapshot baseline);

        /// <summary>
        /// 从预测备份缓冲区恢复 Component Data，仅恢复已序列化字段
        /// </summary>
        /// <param name="component">组件数据</param>
        /// <param name="backup">备份缓冲区</param>
        void RestoreFromBackupGenerated(ref TComponent component, in TComponent backup);

#if UNITY_EDITOR || NETCODE_DEBUG
        /// <summary>
        /// 计算此 Component 的预测误差
        /// </summary>
        /// <param name="component">组件数据</param>
        /// <param name="backup">备份缓冲区</param>
        /// <param name="errorsList">错误数据</param>
        /// <param name="errorsCount">错误数量</param>
        void ReportPredictionErrorsGenerated(in TComponent component, in TComponent backup, IntPtr errorsList,
            int errorsCount);
#endif
    }
}
