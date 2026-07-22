using System;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于升级到新 Component 类型的临时类型，应在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("OutgoingRpcDataStreamBufferComponent has been deprecated. Use OutgoingRpcDataStreamBuffer instead (UnityUpgradable) -> OutgoingRpcDataStreamBuffer", true)]
    [InternalBufferCapacity(0)]
    public struct OutgoingRpcDataStreamBufferComponent
    {
        /// <summary>
        /// 元素值
        /// </summary>
        public byte Value;
    }
    /// <summary>
    /// 用于升级到新 Component 类型的临时类型，应在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("IncomingRpcDataStreamBufferComponent has been deprecated. Use IncomingRpcDataStreamBuffer instead (UnityUpgradable) -> IncomingRpcDataStreamBuffer", true)]
    [InternalBufferCapacity(0)]
    public struct IncomingRpcDataStreamBufferComponent
    {
        /// <summary>
        /// 元素值
        /// </summary>
        public byte Value;
    }

    /// <summary>
    /// 每个 NetworkConnection 一个，用于存储队列中的出站 RPC 数据
    /// 因此缓冲区大小与客户端创建的 RPC 数量乘以单条大小有关
    /// RPC 大小可能不同，且不希望持续将 RPC 数据移入或移出 Chunk，
    /// 因而 InternalBufferCapacity 设为 0
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct OutgoingRpcDataStreamBuffer : IBufferElementData
    {
        /// <summary>
        /// 元素值
        /// </summary>
        public byte Value;
    }

    /// <summary>
    /// 每个 NetworkConnection 一个，用于存储队列中的入站 RPC 数据
    /// 因此缓冲区大小与来自服务器的 RPC 数量乘以单条大小有关
    /// RPC 大小可能不同，且不希望持续将 RPC 数据移入或移出 Chunk，
    /// 因而 InternalBufferCapacity 设为 0
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct IncomingRpcDataStreamBuffer : IBufferElementData
    {
        /// <summary>
        /// 元素值
        /// </summary>
        public byte Value;
    }
}
