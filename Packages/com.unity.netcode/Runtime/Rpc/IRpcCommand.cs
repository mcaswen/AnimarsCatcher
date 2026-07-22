using Unity.Collections;
using Unity.Entities;


namespace Unity.NetCode
{
    /// <summary>
    /// <para>用于声明 <see cref="NetworkStreamProtocol.Rpc"/> 结构体的接口</para>
    /// <para>
    /// RPC 是客户端和服务器都能收发的一次性消息，可用于实现大厅、关卡加载逻辑、请求生成玩家等用途
    /// 与 Ghost <see cref="SnapshotData"/> 不同，RPC 消息通过<b>专用可靠通道</b>发送，因此保证能够收到
    /// </para>
    /// <para>
    /// RPC 是可靠消息，不应替代 Ghost，也不应用于发送频繁变化的数据或玩家 Command，
    /// 参见 <see cref="ICommandData"/> 和 <see cref="IInputComponentData"/>
    /// <b>原因如下</b>
    /// 1）任意时刻允许在途的可靠数据包数量存在上限
    /// 2）可靠 Pipeline 的顺序保证会引入延迟
    /// </para>
    /// <para>
    /// RPC 结构体可以包含任意数量的 Burst 兼容字段，但序列化后的大小必须能够装入单个数据包
    /// 不支持大型消息，请参阅 <see cref="NetworkParameterConstants.MaxMessageSize"/> 并计入 Header 大小
    /// </para>
    /// <para>
    /// 可以创建自定义 <see cref="INetworkStreamDriverConstructor"/> 并增大 MaxMessageSize，
    /// 或向可靠 Pipeline 通道加入 <see cref="FragmentationPipelineStage"/>，以部分缓解此限制
    /// 前一种方法只会在理想环境和网络条件下工作，必须进行充分测试
    /// </para>
    /// <para>
    /// <b>用法：</b>要发送使用 <see cref="IRpcCommand"/> 接口声明的 RPC，
    /// 应创建一个同时带有 RPC 消息组件和 <see cref="SendRpcCommandRequest"/> 的新 Entity
    /// <i>后者会通知 NetCode 系统该 RPC 已存在并将其发送</i>
    /// 最好使用 Archetype 完成此操作，以避免运行时结构性变更
    /// </para>
    /// <code>
    /// m_RpcArchetype = EntityManager.CreateArchetype(..);
    ///
    /// var ent = EntityManager.CreateEntity(m_RpcArchetype);
    /// EntityManager.SetComponentData(new MyRpc { SomeData = 5 });
    /// </code>
    /// <para>
    /// 使用 <see cref="IRpcCommand"/> 声明 RPC 后，系统会自动生成序列化代码，
    /// 以及处理 <see cref="SendRpcCommandRequest"/> 请求所需的其他模板代码
    /// 例如
    /// </para>
    /// <code>
    /// public struct MyRpc : IRpcCommand
    /// {
    ///    public int SomeData;
    /// }
    /// </code>
    /// <para>将生成以下系统和结构体</para>
    /// <para>- 为该 RPC 类型实现 <see cref="IRpcCommandSerializer{T}"/> 的结构体</para>
    /// <para>- 负责消费 <see cref="SendRpcCommandRequest"/> 请求，并将消息排入发送连接的
    /// <see cref="OutgoingRpcDataStreamBuffer"/> 数据流的系统，通过
    /// <see cref="RpcQueue{TActionSerializer,TActionRequest}"/> 调用
    /// </para>
    /// <para>
    /// 由于序列化代码由 Source Generator 生成，只有代码生成系统能够识别，
    /// 且允许在 Command 和 RPC 中使用的类型才会被序列化
    /// 更多信息请参阅 <see cref="Unity.NetCode.Generators.TypeRegistryEntry"/>
    /// </para>
    /// <para>
    /// <see cref="OutgoingRpcDataStreamBuffer"/> 会在模拟帧末尾由 <see cref="RpcSystem"/> 处理，
    /// 队列中的所有消息都会尝试通过网络发送，前提是上述可靠缓冲区尚未填满
    /// </para>
    /// <para>
    /// <b>关于广播 RPC 与发送给特定客户端的 RPC 之间的区别，请参阅 <see cref="SendRpcCommandRequest"/></b>
    /// </para>
    /// </summary>
    /// <remarks>
    /// RPC 不保证相对 Ghost Snapshot 的到达顺序
    /// 例如先发送 RPC 再发送 Snapshot 时，必须假定二者可能按<i>任意</i>顺序到达
    /// 但<b>所有 RPC 网络消息都会严格按照其“发送”顺序接收，而不是按照其“触发”顺序</b>
    /// </remarks>
    public interface IRpcCommand : IComponentData
    {}

    /// <summary>
    /// 用于实现连接审批流程所需 RPC 的接口
    /// 处于 <see cref="ConnectionState.State.Handshake"/> 和/或 <see cref="ConnectionState.State.Approval"/> 状态时，
    /// 只允许收发 <see cref="IApprovalRpcCommand"/> Command
    /// <br/>服务器可以选择要求所有传入连接都经过连接审批
    /// 只有服务器收到并验证通过 Approval RPC Payload 后，连接流程才能继续
    /// Approval Token 由具体游戏定义，因此服务器收到有效的 <see cref="IApprovalRpcCommand"/> 后，
    /// NetCode 期望用户代码向 Connection Entity 添加 <see cref="ConnectionApproved"/>
    /// </summary>
    public interface IApprovalRpcCommand : IComponentData
    {}

    /// <summary>
    /// 用于向 <see cref="IRpcCommandSerializer{T}.Serialize"/> 方法传递附加数据的互操作结构体
    /// </summary>
    public struct RpcSerializerState
    {
        /// <summary>
        /// 从 Entity 获取 <see cref="GhostInstance"/> 的只读访问器
        /// 用于序列化已复制的 Ghost Entity 引用
        /// </summary>
        public ComponentLookup<GhostInstance> GhostFromEntity;

        /// <summary>
        /// 用于获取分配给 RPC 使用的 <see cref="StreamCompressionModel"/> 的只读映射
        /// </summary>
        public StreamCompressionModel CompressionModel;
    }

    /// <summary>
    /// 用于向 <see cref="IRpcCommandSerializer{T}.Deserialize"/> 方法传递附加数据的互操作结构体
    /// </summary>
    public struct RpcDeserializerState
    {
        /// <summary>
        /// 用于获取绑定到指定 <see cref="SpawnedGhost"/> 的 Entity 的只读映射
        /// 用于反序列化已复制的 Ghost Entity 引用
        /// </summary>
        public NativeParallelHashMap<SpawnedGhost, Entity>.ReadOnly ghostMap;

        /// <summary>
        /// 用于获取分配给 RPC 使用的 <see cref="StreamCompressionModel"/> 的只读映射
        /// </summary>
        public StreamCompressionModel CompressionModel;
    }

    /// <summary>
    /// <para>Burst 兼容结构体为序列化和反序列化指定 <typeparamref name="T"/> 类型而必须实现的接口</para>
    /// <para>常见做法是让声明 RPC 的结构体同时实现序列化与反序列化接口
    /// 例如
    /// </para>
    /// <code>
    /// struct MyRpc : IComponentData, IRpcCommandSerializer{MyRpc}
    /// {
    ///     public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in MyRpc data)
    ///     { ... }
    ///     public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref MyRpc data)
    ///     { ... }
    ///     PortableFunctionPointer{RpcExecutor.ExecuteDelegate} CompileExecute()
    ///     { ... }
    /// }
    /// </code>
    /// <para>
    /// 使用 <see cref="IRpcCommand"/> 接口声明 RPC 时，无需自行实现 `IRpcCommandSerializer` 接口
    /// 代码生成系统会自动创建实现该接口的结构体和全部必要模板代码
    /// </para>
    /// </summary>
    /// <typeparam name="T">要序列化的 Component 类型</typeparam>
    public interface IRpcCommandSerializer<T> where T: struct, IComponentData
    {
        /// <summary>
        /// RPC 从 <see cref="OutgoingRpcDataStreamBuffer"/> 出队并准备通过网络发送时，
        /// 由 <see cref="RpcSystem"/> 调用的方法
        /// 结构体实现 <see cref="IRpcCommand"/> 接口后，序列化代码会自动生成
        /// 选择手动序列化时必须自行实现此方法
        /// </summary>
        /// <param name="writer">数据写入器</param>
        /// <param name="state">序列化器状态</param>
        /// <param name="data">要序列化的数据</param>
        void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in T data);
        /// <summary>
        /// RPC 从 <see cref="IncomingRpcDataStreamBuffer"/> 出队时，由 <see cref="RpcSystem"/> 调用的方法
        /// 它会将数据从 <paramref name="reader"/> 复制到输出 <paramref name="data"/>
        /// 结构体实现 <see cref="IRpcCommand"/> 接口后，反序列化代码会自动生成
        /// 选择手动序列化时必须自行实现此方法
        /// </summary>
        /// <param name="reader">数据读取器</param>
        /// <param name="state">反序列化器状态</param>
        /// <param name="data">用于接收读取结果的数据</param>
        void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref T data);
        /// <summary>
        /// RPC 在运行时注册到 <see cref="RpcSystem"/> 时调用
        /// 使用 <see cref="IRpcCommand"/> 声明 RPC 后，此方法会自动生成
        /// 关于如何使用它实现自定义执行方法，请参阅 <see cref="RpcExecutor"/>
        /// </summary>
        /// <returns>指向有效 Burst 兼容静态方法的函数指针，该方法会在 RPC 反序列化后被调用以真正执行 Command</returns>
        PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute();
    }
}
