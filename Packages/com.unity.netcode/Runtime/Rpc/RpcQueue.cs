using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>
    /// 用于调度出站 RPC 的辅助结构体
    /// RpcQueue 由消费 <see cref="SendRpcCommandRequest"/> 请求的代码生成系统在内部使用，
    /// 它会序列化 RPC，并将其写入发送连接的 <see cref="OutgoingRpcDataStreamBuffer"/>
    /// </para>
    /// <para>
    /// 自定义系统可以调用 <see cref="RpcCollection.GetRpcQueue{TActionRequest,TActionSerializer}"/>，
    /// 从 <see cref="RpcCollection"/> 获取指定 <typeparamref name="TActionRequest"/>、
    /// <typeparamref name="TActionSerializer"/> 类型对对应的 RpcQueue 实例
    /// </para>
    /// </summary>
    /// <remarks>
    /// 如果要缓存获取到的队列，例如在系统的 OnCreate 函数中缓存，
    /// 必须使用 <see cref="CreateAfterAttribute"/> 确保该系统在 <see cref="RpcSystem"/> 之后创建
    /// </remarks>
    /// <typeparam name="TActionSerializer">为 <typeparamref name="TActionRequest"/> 实现 <see cref="IRpcCommandSerializer{T}"/> 接口的结构体类型名</typeparam>
    /// <typeparam name="TActionRequest">实现 <see cref="IComponentData"/> 接口的结构体类型名</typeparam>
    public struct RpcQueue<TActionSerializer, TActionRequest>
        where TActionRequest : struct, IComponentData
        where TActionSerializer : struct, IRpcCommandSerializer<TActionRequest>
    {
        internal ulong rpcType;
        [ReadOnly] internal NativeParallelHashMap<ulong, int> rpcTypeHashToIndex;
        [ReadOnly] internal NativeReference<byte> dynamicAssemblyList;

        /// <summary>
        /// <para>
        /// 为指定连接序列化新的 RPC 数据包，并将其追加到 <see cref="OutgoingRpcDataStreamBuffer"/>，
        /// 从而调度该 RPC 通过网络发送
        /// </para>
        /// <para>
        /// RPC 二进制数据采用以下格式</para>
        /// <para> - PacketType：根据 <see cref="RpcCollection.DynamicAssemblyList"/> 使用 short 或 long</para>
        /// <para> - MsgLen：short，表示序列化数据的长度</para>
        /// <para> - RpcData：调用 <typeparamref name="TActionSerializer"/> 序列化方法生成的二进制数据</para>
        /// </summary>
        /// <param name="buffer">RPC 数据包的 Stream Buffer</param>
        /// <param name="ghostFromEntity">GhostInstance 查询</param>
        /// <param name="data">要发送的数据</param>
        /// <exception cref="InvalidOperationException">找不到该 RPC 类型对应的 RPC 索引时抛出</exception>
        public unsafe void Schedule(DynamicBuffer<OutgoingRpcDataStreamBuffer> buffer,
            ComponentLookup<GhostInstance> ghostFromEntity, TActionRequest data) // TODO-2.0：data 应使用 in，但如果重构为指针操作，则应保留副本
        {
            var serializer = default(TActionSerializer);
            // TODO：为 RPC 和 Ghost 公开用户可配置的 StreamCompressionModel，并在此接入
            var serializerState = new RpcSerializerState
            {
                GhostFromEntity = ghostFromEntity,
                CompressionModel = StreamCompressionModel.Default,
            };
            var msgHeaderLenBytes = RpcCollection.GetInnerRpcMessageHeaderLength(dynamicAssemblyList.Value == 1);
            int maxSizeBytes = UnsafeUtility.SizeOf<TActionRequest>() + msgHeaderLenBytes + 1;
            int rpcIndex = 0;
            if (!(dynamicAssemblyList.Value == 1) && !rpcTypeHashToIndex.TryGetValue(rpcType, out rpcIndex))
                throw new InvalidOperationException($"Could not find RPC index for type '{rpcType}'!");
            while (true)
            {
                DataStreamWriter writer = new DataStreamWriter(maxSizeBytes, Allocator.Temp);
                if (dynamicAssemblyList.Value == 1)
                    writer.WriteULong(rpcType);
                else
                    writer.WriteUShort((ushort)rpcIndex);

                var lenWriter = writer;
                writer.WriteUShort((ushort)0);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                UnityEngine.Debug.Assert(writer.Length == RpcCollection.GetInnerRpcMessageHeaderLength(dynamicAssemblyList.Value == 1));
#endif

                serializer.Serialize(ref writer, serializerState, data);

                if (!writer.HasFailedWrites)
                {
                    // 如果此 RPC 使用 Delta Compression，1.3 版本支持，则必须 Flush 并向上对齐到字节，
                    // 同时必须以位为单位存储 RPC 长度
                    var rpcDataSizeBits = (writer.LengthInBits - (msgHeaderLenBytes * 8));
                    writer.Flush();

                    // 注意：单个 RPC 达到 8KiB 显然不合理，但应由 Transport 通过 BeginSend 告知 `RpcSystem` 最大包大小，
                    // 同时用户也可以选择启用分片
                    if (rpcDataSizeBits > ushort.MaxValue)
                        throw new InvalidOperationException($"Individual RPC (of type {ComponentType.ReadOnly<TActionRequest>().ToFixedString()}) is too large to serialize into the RpcQueue! It is {rpcDataSizeBits} bits [8192 bytes], which is greater than ushort.MaxValue of {ushort.MaxValue}!");
                    lenWriter.WriteUShort((ushort) rpcDataSizeBits);

                    var prevLen = buffer.Length;
                    var desiredLength = buffer.Length + writer.Length;
                    buffer.ResizeUninitialized(desiredLength);
                    byte* ptr = (byte*) buffer.GetUnsafePtr();
                    ptr += prevLen;
                    UnsafeUtility.MemCpy(ptr, writer.AsNativeArray().GetUnsafeReadOnlyPtr(), writer.Length);
                    break;
                }
                maxSizeBytes *= 2;
            }
        }
    }
}
