using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.HostMigration;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>NetworkProtocolVersion 是由 <see cref="GhostCollectionSystem"/> 在 GhostCollection 就绪后自动创建的 Singleton Entity，
    /// 用于验证客户端与服务器的兼容性</para>
    /// <para>
    /// 协议版本由以下部分组成：</para>
    /// <para>- NetCode 包版本</para>
    /// <para>- 用户定义的 <see cref="GameProtocolVersion"/> 游戏版本，用于标识游戏版本</para>
    /// <para>- 全部 <see cref="IRpcCommand"/> 和 <see cref="ICommandData"/> 的唯一 Hash，
    /// 用于验证客户端与服务器识别相同的 RPC 和 Command，并能以相同方式序列化和反序列化</para>
    /// <para>- 全部已复制 <see cref="IComponentData"/> 和 <see cref="IBufferElementData"/> 的唯一 Hash，
    /// 用于验证客户端与服务器都能序列化和反序列化 Ghost 中的全部复制组件</para>
    /// <para>
    /// 客户端尝试连接服务器时，双方会在初始握手期间交换协议版本，验证是否使用相同版本
    /// 如果版本不匹配，连接会被强制关闭
    /// </para>
    /// </summary>
    public struct NetworkProtocolVersion : IComponentData
    {
        /// <summary>
        /// 用于判断 NetCode 包版本是否兼容的整数
        /// </summary>
        /// <remarks>
        /// 注意：递增此值表示 NetCode 与以前的版本不兼容
        /// 但连接到不兼容版本时不保证能够得到友好的错误信息，
        /// 因为如果协议版本的序列化方式发生变化，例如修改 RPC Header 大小，几乎肯定无法正确反序列化此值
        /// <br/><b>注意：NetCode 不保证任何不同主版本、次版本或补丁版本彼此兼容，
        /// 只保证完全相同的版本与自身兼容</b>
        /// </remarks>
        public const int k_NetCodeVersion = 2;

        /// <summary>
        /// NetCode 包版本
        /// </summary>
        public int NetCodeVersion;
        /// <summary>
        /// 服务器和客户端使用的用户自定义游戏版本
        /// 默认值为 0，除非使用 <see cref="GameProtocolVersion"/> 自定义
        /// </summary>
        public int GameVersion;
        /// <summary>
        /// 根据全部 RPC 和 Command 计算的唯一 Hash
        /// 用于检查服务器与客户端是否具有相同消息，以及兼容的数据与序列化方式
        /// </summary>
        public ulong RpcCollectionVersion;
        /// <summary>
        /// 根据全部序列化组件计算的唯一 Hash，用于检查客户端能否正确解码 Snapshot
        /// </summary>
        public ulong ComponentCollectionVersion;

        /// <summary>
        /// 在遵循 <see cref="RpcCollection.DynamicAssemblyList"/> 规则的前提下，判断两个版本是否匹配
        /// </summary>
        /// <param name="other"></param>
        /// <param name="useDynamicAssemblyList">
        /// DynamicAssemblyList 表示忽略 RpcCollectionVersion 和 ComponentCollectionVersion
        /// 只要正在使用的每个 RPC 和 Ghost 都具有客户端与服务器共同识别的 Hash，二者就可以不同</param>
        /// <returns></returns>
        internal bool IsCorrect(NetworkProtocolVersion other, bool useDynamicAssemblyList)
        {
            var matchesRequiredFields = NetCodeVersion == other.NetCodeVersion && GameVersion == other.GameVersion;
            if (useDynamicAssemblyList) return matchesRequiredFields;
            return matchesRequiredFields && RpcCollectionVersion == other.RpcCollectionVersion
                                         && ComponentCollectionVersion == other.ComponentCollectionVersion;
        }

        /// <summary>
        /// 辅助方法
        /// </summary>
        /// <returns>"NPV[NetCodeVersion:0, GameVersion:0, RpcCollection:00000000000, ComponentCollection:00000000000]"</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString512Bytes ToFixedString() => $"NPV[NetCodeVersion:{NetCodeVersion}, GameVersion:{GameVersion}, RpcCollection:{RpcCollectionVersion}, ComponentCollection:{ComponentCollectionVersion}]";

        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();

        /// <summary>
        /// 确保不会写入无效值
        /// </summary>
        [Conditional("UNITY_ASSERTIONS")]
        internal void AssertIsValid()
        {
            UnityEngine.Debug.Assert(NetCodeVersion != 0, nameof(NetCodeVersion));
            // GameVersion 为 0 是有效情况
            UnityEngine.Debug.Assert(RpcCollectionVersion != 0, nameof(RpcCollectionVersion));
            UnityEngine.Debug.Assert(ComponentCollectionVersion != 0, nameof(ComponentCollectionVersion));
        }
    }

    /// <summary>
    /// 客户端与服务器连接时用于协议验证的游戏专用版本
    /// 如果不存在具有此组件的 Singleton，则改用 0
    /// 协议验证仍会检查 <see cref="NetworkProtocolVersion.NetCodeVersion"/>、
    /// <see cref="NetworkProtocolVersion.RpcCollectionVersion"/> 和 <see cref="NetworkProtocolVersion.ComponentCollectionVersion"/>
    /// </summary>
    public struct GameProtocolVersion : IComponentData
    {
        /// <summary>
        /// 用户定义的当前游戏版本标识整数
        /// </summary>
        public int Version;
    }

     /// <summary>
    /// 系统 RPC：Transport 层报告连接建立成功后，每个 World 会立即发送此 RPC，
    /// 声明各自认定的 <see cref="NetworkProtocolVersion"/>
    /// 如果双方一致，服务器会回复 <see cref="ServerApprovedConnection"/>；
    /// 如果启用了审批流程，则回复 <see cref="ServerRequestApprovalAfterHandshake"/>
    /// </summary>
    [BurstCompile]
    internal struct RequestProtocolVersionHandshake : IApprovalRpcCommand, IRpcCommandSerializer<RequestProtocolVersionHandshake>
    {
        public NetworkProtocolVersion Data;
        public uint ConnectionUniqueId;

        /// <summary>
        /// 不要修改此值，除非极少见的 RPC 序列化发生根本变化的情况
        /// 否则会产生无意义的协议版本错误
        /// </summary>
        private const int NetcodeVersionBaseline = 2;
        private const int GameVersionBaseline = 0;

        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in RequestProtocolVersionHandshake data)
        {
            data.Data.AssertIsValid();
            var compressionModel = StreamCompressionModel.Default;
            writer.WritePackedIntDelta(data.Data.NetCodeVersion, NetcodeVersionBaseline, compressionModel);
            writer.WritePackedIntDelta(data.Data.GameVersion, GameVersionBaseline, compressionModel);
            writer.WriteULong(data.Data.RpcCollectionVersion);
            writer.WriteULong(data.Data.ComponentCollectionVersion);
            writer.WriteUInt(data.ConnectionUniqueId);
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref RequestProtocolVersionHandshake data)
        {
            var compressionModel = StreamCompressionModel.Default;
            data.Data.NetCodeVersion = reader.ReadPackedIntDelta(NetcodeVersionBaseline, compressionModel);
            data.Data.GameVersion = reader.ReadPackedIntDelta(GameVersionBaseline, compressionModel);
            data.Data.RpcCollectionVersion = reader.ReadULong();
            data.Data.ComponentCollectionVersion = reader.ReadULong();
            data.ConnectionUniqueId = reader.ReadUInt();
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            // 已收到协议版本，检查它是否正确
            parameters.ProtocolVersion.AssertIsValid();
            var rpcData = default(RequestProtocolVersionHandshake);
            rpcData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref rpcData);

            var protocolVersionIsCorrect = rpcData.Data.IsCorrect(parameters.ProtocolVersion, parameters.UseDynamicAssemblyList);
            parameters.NetDebug.DebugLog($"[{parameters.WorldName}][Connection] Received protocol version {parameters.ConnectionStateRef.Value.ToFixedString()} UDAL:{parameters.UseDynamicAssemblyList} Connection[UniqueId:{rpcData.ConnectionUniqueId}] IsCorrect:{protocolVersionIsCorrect}\n - Ours:{parameters.ProtocolVersion.ToFixedString()}\n - Them:{rpcData.Data.ToFixedString()}");
            if (protocolVersionIsCorrect)
            {
                // 标记此连接可以进行 Handshake
                parameters.ConnectionStateRef.ProtocolVersionReceived = 1;
                // 客户端上报唯一连接 ID 表示它正在重连，将此 ID 分配给服务器上的客户端连接 Entity
                // 稍后分配新唯一 ID 时，服务器会发现客户端已有 ID 并跳过分配
                if (rpcData.ConnectionUniqueId != 0)
                {
                    if (parameters.IsServer)
                    {
                        parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, new ConnectionUniqueId() { Value = rpcData.ConnectionUniqueId });
                        parameters.CommandBuffer.AddComponent<MigrateComponents>(parameters.JobIndex, parameters.Connection);
                    }
                    parameters.CommandBuffer.AddComponent<NetworkStreamIsReconnected>(parameters.JobIndex, parameters.Connection);
                }
                return;
            }

            // 错误处理流程
            var connectionEntity = parameters.Connection;
            var pveEntity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, pveEntity, new RpcSystem.ProtocolVersionError
            {
                connection = connectionEntity,
                remoteProtocol = rpcData.Data,
            });
        }
        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }
    }

    /// <summary>
    /// 系统 RPC：客户端通过 <see cref="RequestProtocolVersionHandshake"/> 提交正确的
    /// <see cref="NetworkProtocolVersion"/> 后，如果需要审批，服务器会回复此 RPC
    /// 如果不需要审批，则直接进入 <see cref="ServerApprovedConnection"/>
    /// </summary>
    [BurstCompile]
    internal struct ServerRequestApprovalAfterHandshake : IApprovalRpcCommand, IRpcCommandSerializer<ServerRequestApprovalAfterHandshake>
    {
        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in ServerRequestApprovalAfterHandshake data)
        {
        }

        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref ServerRequestApprovalAfterHandshake data)
        {
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            // RPC 已到达客户端，客户端必须进入 Approval 状态
            var rpcData = default(ServerRequestApprovalAfterHandshake);
            rpcData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref rpcData);

            // 反序列化后再验证是否允许执行，避免产生反序列化错误
            if (parameters.IsServer)
            {
                parameters.NetDebug.LogError($"[{parameters.WorldName}][Connection] Server received internal client-only RPC request '{ComponentType.ReadWrite<ServerRequestApprovalAfterHandshake>().ToFixedString()}' from client. This is not allowed, and the client connection will be disconnected.");
                parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, new NetworkStreamRequestDisconnect
                {
                    Reason = NetworkStreamDisconnectReason.InvalidRpc,
                });
                return;
            }

            parameters.NetDebug.DebugLog($"[{parameters.WorldName}][Connection] Client received valid protocol version from server, handshake complete!");
            parameters.ConnectionStateRef.CurrentState = ConnectionState.State.Approval;
            parameters.ConnectionStateRef.CurrentStateDirty = true;
            parameters.CommandBuffer.SetName(parameters.JobIndex, parameters.Connection, "NetworkConnection (Approval)");
        }

        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }
    }
}
