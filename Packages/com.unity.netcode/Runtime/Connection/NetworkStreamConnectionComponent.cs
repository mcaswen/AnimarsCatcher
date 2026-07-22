using System;
using System.Runtime.InteropServices;
using Unity.Entities;

using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Error;

namespace Unity.NetCode
{
    ///<summary>
    /// <para>连接由具有 NetworkStreamConnection 的 Entity 表示
    /// 此组件保存对底层 Transport <see cref="NetworkConnection"/> 以及创建它的 <see cref="NetworkDriver"/> 的引用
    /// 所有连接都具有一组公共组件：</para>
    /// <para>- <see cref="NetworkId"/>，仅在连接建立后存在</para>
    /// <para>- <see cref="IncomingRpcDataStreamBuffer"/></para>
    /// <para>- <see cref="OutgoingCommandDataStreamBuffer"/></para>
    /// <para>- <see cref="OutgoingRpcDataStreamBuffer"/></para>
    /// <para>- <see cref="PrespawnSectionAck"/></para>
    /// <para>- <see cref="CommandTarget"/></para>
    /// <para>客户端连接还具有 <see cref="IncomingSnapshotDataStreamBuffer"/>，用于处理服务器 Ghost Snapshot</para>
    /// <para>单 World Host 和客户端还具有 <see cref="LocalConnection"/>，用于区分本地连接与其他客户端连接
    /// 在 Server World 中，所有连接都是其他客户端连接</para>
    ///</summary>
    /// <remarks>绝不能自行销毁此 Entity，尝试这样做会收到错误</remarks>
    public struct NetworkStreamConnection : ICleanupComponentData
    {
        /// <summary>
        /// 底层 Transport <see cref="NetworkConnection"/>
        /// </summary>
        public NetworkConnection Value;

        /// <summary>
        /// 创建此连接的 Driver 标识符
        /// 可用于从 <see cref="NetworkDriverStore"/> 获取 <see cref="NetworkDriver"/>
        /// 或 <see cref="NetworkDriverStore.NetworkDriverInstance"/>
        /// </summary>
        public int DriverId;

        /// <summary>
        /// 标记连接是否已交换协议版本的标志
        /// 1 表示已经接收并接受远端协议版本，即该版本有效
        /// </summary>
        public int ProtocolVersionReceived;

        /// <summary>
        /// 最近一次从 Driver 获取的状态缓存
        /// </summary>
        /// <remarks>可能已经过期，因为每个 <see cref="SimulationSystemGroup"/> Tick 只刷新一次</remarks>
        public ConnectionState.State CurrentState;

        /// <summary>
        /// 仅服务器使用：服务器接受连接时的时间戳
        /// 随后用于检测连接 Handshake 或 Approval 是否超时
        /// </summary>
        internal uint ConnectionApprovalTimeoutStart;

        /// <summary>
        /// 在事件触发逻辑之外更新状态时，通过此标志让下一次更新能够检测到变化
        /// </summary>
        [MarshalAs(UnmanagedType.U1)]
        internal bool CurrentStateDirty;

        /// <summary>
        /// 辅助属性
        /// </summary>
        internal bool IsHandshakeOrApproval => CurrentState is ConnectionState.State.Handshake or ConnectionState.State.Approval;

        internal static ComponentTypeSet GetEssentialComponentsForConnection()
        {
            return new ComponentTypeSet(
                // 组件
                ComponentType.ReadWrite<CommandTarget>(),

                // 缓冲区
                ComponentType.ReadWrite<IncomingRpcDataStreamBuffer>(),
                ComponentType.ReadWrite<LinkedEntityGroup>()
            );
        }
    }

    /// <summary>
    /// 表示连接应开始发送和接收 Snapshot 与 Command 的组件
    /// 添加此组件前，连接只处理 RPC
    /// 必须由游戏逻辑添加此组件，才能开始发送 Snapshot 和 Command
    /// </summary>
    public struct NetworkStreamInGame : IComponentData
    {
    }

    /// <summary>
    /// 添加到重连连接的 Tag，即客户端断开后重新连接服务器
    /// 服务器端和客户端都会添加
    /// </summary>
    public struct NetworkStreamIsReconnected : IComponentData { }

    /// <summary>
    /// 在此 World 中分配给连接 Entity 的唯一 ID，重连时会发送给服务器
    /// 由于连接 Entity 本身会在断开时销毁，因此该 ID 必须保存在单独的 Singleton Entity 中
    /// </summary>
    struct ConnectionUniqueId : IComponentData
    {
        public uint Value;
    }

    /// <summary>
    /// 把此组件添加到 Server World 的网络连接 Entity，表示它已通过游戏逻辑审批
    /// 连接获批后，客户端会自动添加此组件
    /// </summary>
    /// <remarks>
    /// 典型流程是向服务器发送游戏专用的 <see cref="IApprovalRpcCommand"/>，
    /// 其中包含从游戏后端获取的认证 Secret
    /// 服务器 RPC 处理逻辑需要验证该 Secret，有效时添加此组件，向 NetCode 表示连接已获批准
    /// <br/>如果客户端认证失败，可以等待其超时，或使用
    /// <see cref="NetworkStreamDisconnectReason.ApprovalFailure"/> 手动断开连接
    /// </remarks>
    /// <seealso cref="ClientServerTickRate.HandshakeApprovalTimeoutMS"/>
    public struct ConnectionApproved : IComponentData
    {
    }

    /// <summary>
    /// 逐连接组件，由服务器上的 <see cref="GhostSendSystem"/> 用于强制 Snapshot 使用非默认数据包大小
    /// 必须由游戏逻辑添加到该连接的 NetworkConnection Entity
    /// </summary>
    /// <remarks>
    /// 有助于强制执行指定的 KBPS 目标
    /// 例如：416 字节 * 60 Hz（通过 <see cref="ClientServerTickRate.SimulationTickRate"/>）约等于 200 kbit/s
    /// 但请注意：
    /// - 不包含也不影响 RPC、Command、控制消息或 UDP Header 开销
    /// - 包含 UTP Packet Header 开销
    /// </remarks>
    public struct NetworkStreamSnapshotTargetSize : IComponentData
    {
        /// <summary>
        /// Snapshot 使用的目标数据包大小
        /// 默认大小为 <see cref="NetworkParameterConstants.MaxMessageSize"/> 减去若干 Header
        /// 可以指定大于单个 <see cref="NetworkParameterConstants.MaxMessageSize"/> 的大小，
        /// 此时通过支持分片的 Pipeline 发送 Snapshot 数据，参见 <see cref="NetworkDriverStore.NetworkDriverInstance.unreliableFragmentedPipeline"/>
        /// 此值的上限是 Fragmentation Pipeline 的 Payload 容量，参见 <see cref="Unity.Networking.Transport.Utilities.FragmentationUtility"/>
        /// </summary>
        /// <remarks>
        /// Snapshot 存在最小大小，用于确保部分新建和已销毁 Entity 得到复制，
        /// 并确保每个 Snapshot 至少复制一个 Ghost
        /// 此行为参见 <see cref="GhostChunkSerializer"/>
        /// </remarks>
        public int Value;
    }

    /// <inheritdoc cref="DisconnectReason"/>
    /// <remarks>直接映射到 <see cref="DisconnectReason"/>，并增加 NetCode 专用原因</remarks>
    public enum NetworkStreamDisconnectReason
    {
        /// <inheritdoc cref="DisconnectReason.Default"/>
        ConnectionClose = DisconnectReason.Default,
        /// <inheritdoc cref="DisconnectReason.Timeout"/>
        Timeout = DisconnectReason.Timeout,
        /// <inheritdoc cref="DisconnectReason.MaxConnectionAttempts"/>
        MaxConnectionAttempts = DisconnectReason.MaxConnectionAttempts,
        /// <inheritdoc cref="DisconnectReason.ClosedByRemote"/>
        ClosedByRemote = DisconnectReason.ClosedByRemote,
        /// <summary>
        /// NetCode 专用：检测到未知或意外的 Ghost Hash，表示服务器与客户端不兼容
        /// </summary>
        BadProtocolVersion = 4,
        /// <summary>
        /// NetCode 专用：检测到 RPC Hash 不匹配或未知 RPC，表示服务器与客户端不兼容
        /// </summary>
        InvalidRpc = 5,
        /// <inheritdoc cref="DisconnectReason.AuthenticationFailure"/>
        AuthenticationFailure = DisconnectReason.AuthenticationFailure,
        /// <inheritdoc cref="DisconnectReason.ProtocolError"/>
        ProtocolError = DisconnectReason.ProtocolError,
        /// <summary>
        /// NetCode 专用：客户端内部 NetCode 逻辑未能在指定 <see cref="ClientServerTickRate.HandshakeApprovalTimeoutMS"/>
        /// 内向服务器发送 <see cref="RequestProtocolVersionHandshake"/>
        /// </summary>
        /// <remarks>请先检查 Approval Timeout 是否过短，否则可能表示 NetCode 发生错误</remarks>
        HandshakeTimeout = 100,
        /// <summary>
        /// NetCode 专用：客户端因提交无效凭据而审批失败
        /// </summary>
        /// <remarks>处理 <see cref="IApprovalRpcCommand"/> RPC 时必须由游戏代码触发</remarks>
        ApprovalFailure = 101,
        /// <summary>
        /// NetCode 专用：客户端未能在指定 <see cref="ClientServerTickRate.HandshakeApprovalTimeoutMS"/> 内获得服务器批准
        /// </summary>
        /// <remarks>换言之，客户端未能在收到请求后发送 <see cref="IApprovalRpcCommand"/> RPC</remarks>
        ApprovalTimeout = 102,
    }

    /// <summary>
    /// 可添加到新建连接以监控其状态变化的可选 Cleanup Component
    /// 必须由 Gameplay 逻辑添加和移除
    /// 存在 <see cref="ConnectionState"/> 时，NetCode 包会在连接状态变化时更新此组件
    /// 添加 ConnectionState 后，会保留连接的 <see cref="NetworkId"/> 和 <see cref="DisconnectReason"/>，
    /// 直到游戏移除此状态组件
    /// </summary>
    /// <remarks>
    /// 此组件可能会被弃用或替换，应优先使用 <see cref="NetworkStreamDriver.ConnectionEventsForTick"/>，因为它：
    /// <list type="bullet">
    /// <item>支持多个消费者，即发布/订阅模型中的多个订阅者</item>
    /// <item>减少样板代码</item>
    /// </list>
    /// </remarks>
    public struct ConnectionState : ICleanupComponentData
    {
        /// <summary>
        /// 连接的当前状态
        /// </summary>
        public enum State
        {
            /// <summary>
            /// 默认状态，连接尚未创建或初始化
            /// </summary>
            Unknown,
            /// <summary>
            /// 连接已关闭
            /// </summary>
            Disconnected,
            /// <summary>
            /// 仅客户端使用，连接正在尝试联系服务器并建立通信通道
            /// </summary>
            Connecting,
            /// <summary>
            /// 客户端已连接服务器，正在交换初始消息，
            /// 例如验证 <see cref="NetworkProtocolVersion"/> 与 <see cref="GameProtocolVersion"/> 是否兼容
            /// </summary>
            Handshake,
            /// <summary>
            /// 连接已在 Transport 层建立，但继续 Handshake 前需要先获得批准
            /// </summary>
            Approval,
            /// <summary>
            /// 连接已建立且 Handshake 已完成，现在处于完全连接状态
            /// 进入此状态时，会向 Network Connection 添加 <see cref="NetworkId"/> 组件
            /// </summary>
            Connected,
        }

        /// <summary>
        /// 连接的当前状态，由 <see cref="NetworkStreamReceiveSystem"/> 在内部更新
        /// </summary>
        public State CurrentState;
        /// <summary>
        /// 分配给连接的 ID，与 <see cref="NetCode.NetworkId"/> 值相同
        /// </summary>
        public int NetworkId;
        /// <summary>
        /// 连接处于 <see cref="State.Disconnected"/> 状态时设置，表示连接终止原因
        /// </summary>
        public NetworkStreamDisconnectReason DisconnectReason;

        /// <summary>
        /// <para>检查两个连接状态是否相等，以下条件必须全部满足：</para>
        /// <para>- <see cref="State"/> 相同</para>
        /// <para>- <see cref="NetworkId"/> 相同</para>
        /// <para>- <see cref="DisconnectReason"/> 相同</para>
        /// </summary>
        /// <param name="other">要比较的组件</param>
        /// <returns>两个连接状态是否相等</returns>
        public bool Equals(ConnectionState other) => CurrentState == other.CurrentState && NetworkId == other.NetworkId && DisconnectReason == other.DisconnectReason;
    }

    /// <summary>
    /// 表示游戏逻辑希望关闭连接的组件
    /// </summary>
    public struct NetworkStreamRequestDisconnect : IComponentData
    {
        /// <summary>
        /// 可选的断开原因，默认值为 <see cref="NetworkStreamDisconnectReason.ConnectionClose"/>
        /// </summary>
        public NetworkStreamDisconnectReason Reason;
    }
    /// <summary>
    /// 可添加到新 Entity 以创建连接的组件，用于替代调用 <see cref="NetworkStreamDriver.Connect"/>
    /// </summary>
    public struct NetworkStreamRequestConnect : IComponentData
    {
        /// <summary>
        /// 远端服务器地址
        /// </summary>
        public NetworkEndpoint Endpoint;
    }

    /// <summary>
    /// 可添加到新 Entity 以开始监听新连接的组件，用于替代调用 <see cref="NetworkStreamDriver.Listen"/>
    /// </summary>
    public struct NetworkStreamRequestListen : IComponentData
    {
        /// <summary>
        /// 远端服务器地址
        /// </summary>
        public NetworkEndpoint Endpoint;
    }

    /// <summary>
    /// 创建请求时可添加到 <see cref="NetworkStreamRequestListen"/> Entity 的可选 Cleanup Component
    /// 用于监控请求状态
    /// 存在此组件时，<see cref="NetworkStreamListenSystem"/> 会在处理请求时更新它
    /// </summary>
    /// <remarks>
    /// 由于它是 Cleanup Component，请求创建者负责正确管理请求 Entity 的生命周期
    /// </remarks>
    public struct NetworkStreamRequestListenResult : ICleanupComponentData
    {
        /// <summary>
        /// 监听请求的状态
        /// </summary>
        public enum State
        {
            /// <summary>
            /// 监听请求仍在等待处理
            /// </summary>
            Pending = 0,
            /// <summary>
            /// 监听请求已成功处理
            /// </summary>
            Succeeded,
            /// <summary>
            /// 监听请求失败，日志中应包含错误信息
            /// </summary>
            Failed,
            /// <summary>
            /// 监听请求被拒绝，因为 Driver 已经在监听
            /// </summary>
            RefusedAlreadyListening,
            /// <summary>
            /// 监听请求被拒绝，因为存在多个请求
            /// </summary>
            RefusedMultipleRequests,
        }
        /// <summary>
        /// 此请求的远端服务器地址
        /// </summary>
        public NetworkEndpoint Endpoint;
        /// <summary>
        /// 请求状态
        /// </summary>
        public State RequestState;
    }

    /// <summary>
    /// 用于升级到新组件类型的临时类型，将在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("IncomingCommandDataStreamBufferComponent has been deprecated. Use IncomingCommandDataStreamBuffer instead (UnityUpgradable) -> IncomingCommandDataStreamBuffer", true)]
    public struct IncomingCommandDataStreamBufferComponent : IBufferElementData
    {
        /// <summary>
        /// Buffer 内容
        /// </summary>
        public byte Value;
    }
    /// <summary>
    /// 用于升级到新组件类型的临时类型，将在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("OutgoingCommandDataStreamBufferComponent has been deprecated. Use OutgoingCommandDataStreamBuffer instead (UnityUpgradable) -> OutgoingCommandDataStreamBuffer", true)]
    public struct OutgoingCommandDataStreamBufferComponent : IBufferElementData
    {
        /// <summary>
        /// Buffer 内容
        /// </summary>
        public byte Value;
    }
    /// <summary>
    /// 用于升级到新组件类型的临时类型，将在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("IncomingSnapshotDataStreamBufferComponent has been deprecated. Use IncomingSnapshotDataStreamBuffer instead (UnityUpgradable) -> IncomingSnapshotDataStreamBuffer", true)]
    public struct IncomingSnapshotDataStreamBufferComponent : IBufferElementData
    {
        /// <summary>
        /// Buffer 内容
        /// </summary>
        public byte Value;
    }

    /// <summary>
    /// 此 Buffer 保存单个入站 Command Packet，每个客户端 NetworkStream 一个
    /// Command Packet 包含 CommandSendSystem.k_InputBufferSendSize 个 Tick 的命令，默认值为 4，
    /// 其中 3 个使用差分压缩
    /// 它还包含用于计算 Ping 的时间戳等信息
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct IncomingCommandDataStreamBuffer : IBufferElementData
    {
        /// <summary>
        /// Buffer 内容
        /// </summary>
        public byte Value;
    }
    /// <summary>
    /// 此 Buffer 保存不包含时间戳和 Ping Header 的单个出站 Command Packet
    /// Command Packet 包含 CommandSendSystem.k_InputBufferSendSize 个 Tick 的命令，默认值为 4，
    /// 其中 3 个使用差分压缩
    /// 它还包含用于计算 Ping 的时间戳等信息
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct OutgoingCommandDataStreamBuffer : IBufferElementData
    {
        /// <summary>
        /// Buffer 内容
        /// </summary>
        public byte Value;
    }

    /// <summary>
    /// 每个 NetworkConnection 一个
    /// 保存连接入站且尚未处理的 Snapshot Stream 数据
    /// 每个 Snapshot 都设计为不超过 <see cref="NetworkParameterConstants.MaxMessageSize"/>，
    /// 因此其大小应小于或等于 MaxMessageSize
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct IncomingSnapshotDataStreamBuffer : IBufferElementData
    {
        /// <summary>
        /// Buffer 内容
        /// </summary>
        public byte Value;
    }


    /// <summary>
    /// 用于标记本地 NetworkConnection Entity
    /// 在单 World Host 中很有用，因为此时通常还具有其他已连接客户端的多个 NetworkConnection 和 Player Entity
    /// 客户端始终具有此组件，纯 Server World 中绝不会存在，单 World 客户端托管服务器中只存在于一个连接上
    /// </summary>
    /// <remarks>
    /// 在单 World Host 中，也可以通过查询 WithAll <see cref="NetworkId"/> 且 WithNone
    /// <see cref="NetworkStreamConnection"/> 的 Entity 查找本地连接，但 LocalConnection 的表达更明确
    /// </remarks>
    public struct LocalConnection : IComponentData { }

    internal static class NetCodeBufferComponentExtensions
    {
        public static unsafe DataStreamReader AsDataStreamReader<T>(this DynamicBuffer<T> self)
            where T: unmanaged, IBufferElementData
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (UnsafeUtility.SizeOf<T>() != 1)
                throw new System.InvalidOperationException("Can only convert DynamicBuffers of size 1 to DataStreamWriters");
#endif
            var na = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(self.GetUnsafeReadOnlyPtr(), self.Length, Allocator.Invalid);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = NativeArrayUnsafeUtility.GetAtomicSafetyHandle(self.AsNativeArray());
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref na, safety);
#endif
            return new DataStreamReader(na);
        }
        public static unsafe void Add<T>(this DynamicBuffer<T> self, ref DataStreamReader reader)
            where T: unmanaged, IBufferElementData
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (UnsafeUtility.SizeOf<T>() != 1)
                throw new System.InvalidOperationException("Can only Add to DynamicBuffers of size 1 from DataStreamReaders");
#endif
            var oldLen = self.Length;
            var length = reader.Length - reader.GetBytesRead();
            self.ResizeUninitialized(oldLen + length);
            reader.ReadBytesUnsafe((byte*)self.GetUnsafePtr() + oldLen, length);
        }
    }
}
