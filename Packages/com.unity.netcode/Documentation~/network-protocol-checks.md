# 网络协议检查

客户端连接服务器时，双方会交换一份协议，即 [NetworkProtocolVersion](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkProtocolVersion.html)，
其中包含 Netcode 版本、游戏版本、RPC 集合和已序列化组件集合。这是一项预防措施，用于阻止不兼容的游戏版本互相连接，避免产生未定义行为

RPC 集合的哈希根据所有已加载程序集内编译的 RPC 计算，计算依据为其类型及成员。类似地，已序列化组件集合由所有已加载程序集中编译并被 Netcode for Entities 识别的 Ghost 组件构成。系统根据 RPC 和已序列化组件的类型及类型成员计算哈希，并将其作为协议的一部分

默认情况下，Netcode for Entities 要求双方交换的协议哈希是确定性的，也就是完全相同，以防止对局中途出现异常，并启用带宽优化

但是，由于该要求非常严格，开发期间经常会将可能兼容的构建错误地判定为不兼容，也就是测试时出现误报。例如，使用独立 Player 测试编辑器内 World 时，编辑器可能加载了一些未包含在构建中的测试程序集，其中可能含有 RPC 类型、Ghost 组件类型或运行时 Ghost 类型，从而造成哈希不匹配并断开连接。因此，可以[禁用](#禁用检查)这项严格的协议版本检查

发生协议版本错误时，每个对等端都会通过 `NetworkStreamDisconnectReason.BadProtocolVersion` 主动断开与远端的连接。用户代码可以读取该原因，并向玩家提示当前构建与目标远端不兼容。在开发构建中，本包还会输出错误日志，列出本地对等端加载的完整且已排序 RPC 与 Ghost 类型。将这些日志与远端产生的日志交叉比对，即可排查类型不匹配问题

## 禁用检查

若要禁用检查，请按如下方式将 [`RpcCollection.DynamicAssemblyList`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.RpcCollection.html#Unity_NetCode_RpcCollection_DynamicAssemblyList)
设为 true：

```csharp
[BurstCompile] // BurstCompile 可选
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
[CreateAfter(typeof(RpcSystem))]
public partial struct SetRpcSystemDynamicAssemblyListSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        SystemAPI.GetSingletonRW<RpcCollection>().ValueRW.DynamicAssemblyList = true;
        state.Enabled = false;
    }
}
```

由于该操作会修改 `RpcCollection`，而它本身由 `RpcSystem` 创建，因此必须在 `RpcSystem.OnCreate` 执行后、`RpcSystem.OnUpdate` 执行前设置此标志。客户端和服务器还必须在开始通信前使用相同的标志值，因为 Netcode 包会根据该值改变 RPC 编码方式，包括 `NetworkProtocolVersion` RPC 本身。尝试连接到标志值不同的 World 会导致类似但不够明确的强制断开错误

> [!NOTE]
> 启用此标志会使每个发送的 RPC 增加六个字节，因为系统将发送完整 RPC 哈希，而不是发送指向确定性查找表的 ushort 索引。这意味着，如果 Netcode 在对局中途收到类型哈希未知的 Ghost 或 RPC，会在运行时抛出错误，之后才强制断开连接。如果客户端在游戏会话开始数小时后突然收到其无法识别的 Ghost 或 RPC，便可能在此时被踢出，而不是在连接握手期间就验证出问题

## 其他资源

- [`NetworkProtocolVersion` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkProtocolVersion.html)
