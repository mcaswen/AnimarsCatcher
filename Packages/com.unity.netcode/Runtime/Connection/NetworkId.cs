using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于升级到新组件类型的临时类型，将在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("NetworkIdComponent has been deprecated. Use NetworkId instead (UnityUpgradable) -> NetworkId", true)]
    public struct NetworkIdComponent : IComponentData
    {}

    /// <summary>
    /// 服务器分配给入站客户端连接的连接标识符
    /// NetworkIdComponent 是当前会话中的临时客户端标识符
    /// 客户端断开后，服务器可以按先到先得的方式复用其 Network ID，并分配给新的入站连接
    /// 因此无法保证断开连接的客户端重连后会获得相同 Network ID
    /// 所以绝不能使用此网络标识符持久化并重新获取指定客户端或玩家的信息
    /// </summary>
    public struct NetworkId : IComponentData, IEquatable<NetworkId>
    {
        /// <summary>
        /// 服务器分配的网络标识符，有效值始终大于 0
        /// </summary>
        public int Value;

        /// <summary>
        /// 返回 `NID[value]`
        /// </summary>
        /// <returns>`NID[value]`</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString32Bytes ToFixedString()
        {
            var s = new FixedString32Bytes((FixedString32Bytes)"NID[");
            s.Append(Value);
            s.Append(']');
            return s;
        }

        /// <inheritdoc cref="ToFixedString"/>>
        public override string ToString() => ToFixedString().ToString();

        /// <inheritdoc cref="IEquatable{T}.Equals(object)"/>
        public static bool operator ==(NetworkId left, NetworkId right) => left.Equals(right);

        /// <inheritdoc cref="IEquatable{T}.Equals(object)"/>
        public static bool operator !=(NetworkId left, NetworkId right) => !left.Equals(right);

        /// <inheritdoc cref="IEquatable{T}.Equals(object)"/>
        public bool Equals(NetworkId other) => this.Value == other.Value;

        /// <inheritdoc cref="IEquatable{T}.Equals(object)"/>
        public override bool Equals(object obj) => obj is NetworkId other && Equals(other);

        /// <inheritdoc cref="object.GetHashCode"/>
        public override int GetHashCode() => Value;
    }

    /// <summary>
    /// 服务器向客户端发送的系统 RPC，用于给新接受的连接分配 <see cref="NetCode.NetworkId"/>
    /// 这表示 <see cref="ConnectionState.State.Handshake"/> 和启用时的 <see cref="ConnectionState.State.Approval"/> 已成功
    /// </summary>
    /// <remarks>
    /// 还负责向客户端传递部分额外的服务器配置信息
    /// 之前名为 `RpcSetNetworkId`
    /// </remarks>
    [BurstCompile]
    internal struct ServerApprovedConnection : IApprovalRpcCommand, IRpcCommandSerializer<ServerApprovedConnection>
    {
        private const uint NetworkIdBaseline = 2;
        public int NetworkId;
        public uint UniqueId;
        public ClientServerTickRateRefreshRequest RefreshRequest;

        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in ServerApprovedConnection data)
        {
            UnityEngine.Debug.Assert(data.NetworkId != 0);

            writer.WritePackedUIntDelta((uint)data.NetworkId, NetworkIdBaseline, state.CompressionModel);
            writer.WriteUInt(data.UniqueId);
            data.RefreshRequest.Serialize(ref writer, in state.CompressionModel);
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref ServerApprovedConnection data)
        {
            data.NetworkId = (int) reader.ReadPackedUIntDelta(NetworkIdBaseline, state.CompressionModel);
            data.UniqueId = reader.ReadUInt();
            data.RefreshRequest.Deserialize(ref reader, in state.CompressionModel);
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            // 客户端收到已成功连接服务器的确认
            var rpcData = default(ServerApprovedConnection);
            rpcData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref rpcData);

            // 反序列化后再验证是否允许执行，避免产生反序列化错误
            if (parameters.IsServer)
            {
                parameters.NetDebug.LogError($"[{parameters.WorldName}][Connection] Server received internal client-only RPC request '{ComponentType.ReadWrite<ServerApprovedConnection>().ToFixedString()}' from client. This is not allowed, and the client connection will be disconnected.");
                parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, new NetworkStreamRequestDisconnect
                {
                    Reason = NetworkStreamDisconnectReason.InvalidRpc,
                });
                return;
            }

            // 按服务器指示设置连接唯一 ID
            if (parameters.ClientConnectionUniqueIdEntity == Entity.Null)
            {
                var uniqueIdEntity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
                parameters.CommandBuffer.AddComponent(parameters.JobIndex, uniqueIdEntity, new ConnectionUniqueId() {Value = rpcData.UniqueId});
            }
            else
            {
                parameters.CommandBuffer.SetComponent(parameters.JobIndex, parameters.ClientConnectionUniqueIdEntity, new ConnectionUniqueId() { Value = rpcData.UniqueId });
                if (parameters.ClientCurrentConnectionUniqueId == rpcData.UniqueId)
                {
                    parameters.CommandBuffer.AddComponent<NetworkStreamIsReconnected>(parameters.JobIndex, parameters.Connection);
                }
            }

            parameters.CommandBuffer.AddComponent<ConnectionApproved>(parameters.JobIndex, parameters.Connection);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, new NetworkId {Value = rpcData.NetworkId});
            parameters.CommandBuffer.AddComponent<LocalConnection>(parameters.JobIndex, parameters.Connection);
            var ent = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, ent, rpcData.RefreshRequest);
            parameters.CommandBuffer.SetName(parameters.JobIndex, parameters.Connection, new FixedString64Bytes(FixedString.Format("NetworkConnection ({0})", rpcData.NetworkId)));
            parameters.NetDebug.DebugLog($"[{parameters.WorldName}][Connection] Client {parameters.Connection.ToFixedString()} received approval from server, we were assigned NetworkId:{rpcData.NetworkId} UniqueId:{rpcData.UniqueId}.");
            parameters.ConnectionStateRef.CurrentState = ConnectionState.State.Connected;
            parameters.ConnectionStateRef.ProtocolVersionReceived = 1;
            parameters.ConnectionStateRef.ConnectionApprovalTimeoutStart = 0;
            parameters.ConnectionStateRef.CurrentStateDirty = true;
        }

        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }
    }
}
