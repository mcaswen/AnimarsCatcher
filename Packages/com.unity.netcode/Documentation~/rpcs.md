# 使用 RPC 通信

使用远程过程调用（RPC）传递高层游戏流程事件，并从客户端向服务器发送一次性的非预测命令。发送端 Job 可以发出 RPC，随后 RPC 在接收端 Job 中执行。这会限制 RPC 内可以执行的操作，例如可读取和修改的数据，以及允许调用的引擎 API。有关 Job System 的详细信息，请参阅 Unity 用户手册中的 [C# Job System](https://docs.unity3d.com/Manual/JobSystem.html)

为了让 Netcode for Entities 中的 RPC 更灵活，可以创建包含特定 Netcode 组件的实体，例如 [`SendRpcCommandRequest`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SendRpcCommandRequest.html) 和 [`ReceiveRpcCommandRequest`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ReceiveRpcCommandRequest.html)。本页将介绍这种方式

<a id="comparing-ghosts-and-rpcs"></a>
## 对比 Ghost 与 RPC

游戏中可以同时使用 [Ghost](ghost-snapshots.md#ghosts) 和 RPC。二者各有更适合的场景，应根据具体需求选择

<a id="ghost-use-cases"></a>
### Ghost 用例

使用 Ghost：

* 复制具有空间局部性、生命周期较短且按实体判断相关性的数据
* 启用 Ghost 实体的[客户端预测](intro-to-prediction.md)，这是隐藏多人游戏延迟最有效的技术

<a id="rpc-use-cases"></a>
### RPC 用例

使用 RPC：

* 传递高层游戏流程事件，例如让所有客户端加载某个关卡
* 从客户端向服务器发送一次性的非预测命令，例如加入小队、发送聊天消息、取消屏蔽某个玩家或请求离开当前区域

<a id="key-differences"></a>
### 关键区别

* RPC 是一次性事件，因此不会自动持久化
    * 例如，宝箱打开时发送 RPC，玩家断开后重新连接，宝箱会看起来仍处于关闭状态
* Ghost 数据会在 Ghost 实体生命周期内保持，Ghost 实体本身的生命周期也会复制。因此，长期存在且可交互的实体应将持久状态保存在 Ghost 组件中
    * 例如，可以把宝箱有限状态机 FSM 作为组件中的 `enum` 保存。玩家打开宝箱、断开再重连后，会重新收到宝箱及其打开状态
* RPC 使用可靠数据包发送，Ghost 快照使用不可靠传输并通过最终一致性收敛
* RPC 数据不经修改直接发送和接收；Ghost 数据会经过差异检测、增量压缩等优化，接收后还可能进行数值平滑
* RPC 不与特定 Tick 或其他快照时序数据绑定，而是在收到它的帧进行处理
* Ghost 快照数据可配合插值和预测及其快照历史，因此支持历史、回滚和重新模拟
* Ghost 快照数据可以通过相关性和重要度优化带宽；RPC 只能广播或发送给单个客户端

<a id="extend-irpccommand"></a>
## 实现 `IRpcCommand`

若要在 Netcode for Entities 中使用 RPC，请创建实现 [`IRpcCommand`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.IRpcCommand.html) 的命令：

```c#
public struct OurRpcCommand : IRpcCommand
{
}
```

如果 RPC 需要携带数据：

```c#
public struct OurRpcCommand : IRpcCommand
{
    public int intData;
    public short shortData;
}
```

源码生成器会生成序列化、反序列化以及注册该 RPC 所需的全部代码

<a id="sending-and-receiving-commands"></a>
## 发送与接收命令

需要创建实体来发送和接收命令。发送命令时，创建实体并添加命令与特殊组件 [`SendRpcCommandRequest`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SendRpcCommandRequest.html)。该组件的 `TargetConnection` 成员指向要接收命令的远程连接

> [!NOTE]
> `TargetConnection` 设为 `Entity.Null` 时，消息会广播给所有客户端。客户端不必设置该值，因为客户端只能向服务器发送 RPC

以下简单系统会在用户按下空格键时发送命令：

```c#
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public class ClientRpcSendSystem : SystemBase
{
    protected override void OnCreate()
    {
        RequireForUpdate<NetworkId>();
    }

    protected override void OnUpdate()
    {
        if (Input.GetKey("space"))
            EntityManager.CreateEntity(typeof(OurRpcCommand), typeof(SendRpcCommandRequest));
    }
}
```

收到 RPC 后，代码生成系统会创建一个可查询的实体。以下系统接收 `OurRpcCommand`：

```c#
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public class ServerRpcReceiveSystem : SystemBase
{
    protected override void OnUpdate()
    {
        Entities.ForEach((Entity entity, ref OurRpcCommand cmd, ref ReceiveRpcCommandRequest req) =>
        {
            PostUpdateCommands.DestroyEntity(entity);
            Debug.Log("收到一条命令");
        }).Run();
    }
}
```

[`RpcSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.RpcSystem.html) 会自动查找全部请求、发送它们，再删除发送请求。在远端，这些请求会表现为带有相同 `IRpcCommand` 和 [`ReceiveRpcCommandRequest`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ReceiveRpcCommandRequest.html) 的实体，后者可用于识别请求来自哪条连接

<a id="creating-an-rpc-without-generating-code"></a>
## 不使用代码生成创建 RPC

RPC 代码生成不是必需的。如果不使用，需要手动创建组件和序列化器；二者可以是同一个结构，也可以分开。以下结构同时作为组件和序列化器：

```c#
[BurstCompile]
public struct OurRpcCommand : IComponentData, IRpcCommandSerializer<OurRpcCommand>
{
    public int SpawnIndex;

    public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in OurRpcCommand data)
    {
        // 示例：相对于值为 2 的基线写入增量
        writer.WritePackedIntDelta(data.SpawnIndex, 2, state.CompressionModel);
    }

    public void Deserialize(ref DataStreamReader reader, in RpcSerializerState state, ref OurRpcCommand data)
    {
        data.SpawnIndex = reader.ReadPackedIntDelta(2, state.CompressionModel);
    }

    public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
    {
        return InvokeExecuteFunctionPointer;
    }

    [BurstCompile(DisableDirectCall = true)]
    private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
    {
    }

    static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
        new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
}
```

[`IRpcCommandSerializer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.IRpcCommandSerializer.html) 接口包含 `Serialize`、`Deserialize` 和 `CompileExecute` 三个方法。`Serialize` 与 `Deserialize` 在数据包中写入和读取数据；`CompileExecute` 使用 Burst 创建 `FunctionPointer`。被编译的函数按引用接收 [`RpcExecutor.Parameters`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.RpcExecutor.Parameters.html)，其中包含执行所需的条目

> [!NOTE]
> 不要直接读取或写入结构自身的字段，也就是不要原地读写；应通过按引用传入的 `data` 参数读写

由于执行函数是静态的，执行 RPC 前需要使用 `Deserialize` 读取结构数据。RPC 随后可以通过命令缓冲区修改连接实体，或创建新的请求实体以执行更复杂的任务；实际命令会在稍后的另一个系统中应用。因此，接收 RPC 无需额外操作，接收端会自动调用其执行方法

若要创建承载 RPC 的实体，请使用 `ExecuteCreateRequestComponent<T>`。可以将前面的 `InvokeExecute` 扩展为：

```c#
[BurstCompile(DisableDirectCall = true)]
private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
{
    RpcExecutor.ExecuteCreateRequestComponent<OurRpcCommand, OurRpcCommand>(ref parameters);
}
```

这会创建带有 [`ReceiveRpcCommandRequest`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ReceiveRpcCommandRequest.html) 和 `OurRpcCommand` 组件的实体

> [!NOTE]
> 如果不需要接收 RPC 实体，就不必在此创建。例如，对于表示新聊天消息的 RPC，直接把消息追加到 `NetworkConnection` 实体的缓冲区，再由系统消费该缓冲区可能更简单

创建 [`IRpcCommandSerializer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.IRpcCommandSerializer.html) 后，需要确保 [`RpcCommandRequest`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.RpcCommandRequestSystem-1.html) 系统能够处理它。可以创建如下系统：

```c#
[UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
[CreateAfter(typeof(RpcSystem))]
[BurstCompile]
partial struct OurRpcCommandRequestSystem : ISystem
{
    RpcCommandRequest<OurRpcCommand, OurRpcCommand> m_Request;

    [BurstCompile]
    struct SendRpc : IJobChunk
    {
        public RpcCommandRequest<OurRpcCommand, OurRpcCommand>.SendRpcData data;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            Assert.IsFalse(useEnabledMask);
            data.Execute(chunk, unfilteredChunkIndex);
        }
    }

    public void OnCreate(ref SystemState state)
    {
        m_Request.OnCreate(ref state);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var sendJob = new SendRpc { data = m_Request.InitJobData(ref state) };
        state.Dependency = sendJob.Schedule(m_Request.Query, state.Dependency);
    }
}
```

`RpcCommandRequest` 系统内部使用 [`RpcQueue`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.RpcQueue-1.html) 调度传出 RPC

<a id="serializing-rpcs"></a>
## 序列化 RPC

如果需要在 `RpcCommand` 上携带数据，请将数据添加为命令成员，再使用 `Serialize` 和 `Deserialize` 决定序列化哪些内容。例如：

```c#
[BurstCompile]
public struct OurDataRpcCommand : IComponentData, IRpcCommandSerializer<OurDataRpcCommand>
{
    public int intData;
    public short shortData;

    public void Serialize(ref DataStreamWriter writer, in OurDataRpcCommand data)
    {
        writer.WriteInt(data.intData);
        writer.WriteShort(data.shortData);
    }

    public void Deserialize(ref DataStreamReader reader, ref OurDataRpcCommand data)
    {
        data.intData = reader.ReadInt();
        data.shortData = reader.ReadShort();
    }

    public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
    {
        return InvokeExecuteFunctionPointer;
    }

    [BurstCompile(DisableDirectCall = true)]
    private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
    {
        RpcExecutor.ExecuteCreateRequestComponent<OurDataRpcCommand, OurDataRpcCommand>(ref parameters);
    }

    static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
        new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
}
```

> [!NOTE]
> `Serialize` 和 `Deserialize` 调用必须对称。上例先写入 `int`，再写入 `short`，读取时也必须按相同顺序读取 `int` 和 `short`。遗漏读取、忘记写入或改变读写顺序都可能引发问题

<a id="rpcqueue"></a>
## `RpcQueue`

[`RpcQueue`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.RpcQueue-1.html) 在内部用于调度传出 RPC，也可以手动创建队列并通过它调度 RPC

调用 `GetSingleton<RpcCollection>().GetRpcQueue<OurRpcCommand, OurRpcCommand>()` 获取队列。可以在 `OnUpdate` 中调用，也可以在 `OnCreate` 中调用并在应用生命周期内缓存。如果在 `OnCreate` 调用，必须确保调用系统在 `RpcSystem` 之后创建

获得队列后，从连接实体获取 `OutgoingRpcDataStreamBuffer`，再调用 `rpcQueue.Schedule(rpcBuffer, new OurRpcCommand())` 调度事件。以下示例在用户按下空格键时通过 `RpcQueue` 发送 RPC：

```c#
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public class ClientQueueRpcSendSystem : ComponentSystem
{
    protected override void OnCreate()
    {
        RequireForUpdate<NetworkId>();
    }

    protected override void OnUpdate()
    {
        if (Input.GetKey("space"))
        {
            var rpcQueue = GetSingleton<RpcCollection>().GetRpcQueue<OurRpcCommand, OurRpcCommand>();
            Entities.ForEach((Entity entity, ref NetworkStreamConnection connection) =>
            {
                var rpcFromEntity = GetBufferLookup<OutgoingRpcDataStreamBuffer>();
                if (rpcFromEntity.Exists(entity))
                {
                    var buffer = rpcFromEntity[entity];
                    rpcQueue.Schedule(buffer, new OurRpcCommand());
                }
            });
        }
    }
}
```
