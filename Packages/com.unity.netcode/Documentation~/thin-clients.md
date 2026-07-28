# 使用瘦客户端进行测试

瘦客户端是一种测试与调试工具，可在编辑器中让模拟的虚拟客户端与正常客户端和服务器 World 一同运行

这类客户端经过大幅精简，应尽可能少地运行逻辑，以免测试时对 CPU 造成很大负载
每添加一个瘦客户端，每帧都会增加少量计算工作

只有显式配置为在瘦客户端 World 中运行的系统才会执行，这些系统的 `WorldSystemFilter` 特性带有 `WorldSystemFilterFlags.ThinClientSimulation` 标志
瘦客户端数据不会进行渲染，因此在表现层中不可见

某些情况下，可能需要检查系统逻辑是否应为瘦客户端运行，并提前退出或取消处理
此时可以使用 `World.IsThinClient()` 扩展方法。请注意，`World.IsClient` 对瘦客户端和完整客户端都会返回 true

## 瘦客户端工作流建议

瘦客户端可以通过多种方式协助测试多人游戏。建议采用以下用法：

* 使用瘦客户端快速测试客户端流程，例如队伍分配、生成位置、排行榜和 UI 等
* 在构建出的 Player 中创建瘦客户端，对游戏服务器进行压力测试和浸泡测试。例如，可以添加配置选项，在正常客户端 World 旁自动创建 `n` 个瘦客户端 World。让每个瘦客户端“跟随主客户端”，自动尝试加入主客户端 World 使用的同一 IP 地址和端口。这样便可利用现有 UI 流程，例如匹配、大厅和 Relay，将这些瘦客户端接入压力测试目标服务器
* 使用第二输入源控制瘦客户端。多人游戏通常包含复杂的 PvP 交互，因此经常需要 AI 在客户端与其交互时执行特定动作，例如蹲伏、趴下、跳跃、向斜后方跑、装填、启用护盾或激活能力。将瘦客户端控制绑定到键盘命令，可以在无需组织试玩或第二名开发人员的情况下测试这些场景。也可以让瘦客户端镜像测试人员的输入，同样能获得良好效果

## 瘦客户端示例

- [NetcodeSamples > HelloNetcode > ThinClient](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/HelloNetcode/2_Intermediate/06_ThinClients)
- [NetcodeSamples > Asteroids](https://github.com/Unity-Technologies/EntityComponentSystemSamples/blob/f22bb949b3865c68d5fc588a6e8d032096dc788a/NetcodeSamples/Assets/Samples/Asteroids/Client/Systems/InputSystem.cs#L66)

## 为瘦客户端设置输入

瘦客户端无法直接使用 `AutoCommandTarget`，因为 `AutoCommandTarget` 要求同一个 Ghost 同时存在于客户端和服务器上，而瘦客户端不会创建 Ghost。因此，需要自行设置连接实体上的 `CommandTarget` 组件

`IInputComponentData` 是最新的输入 API。它会自动将输入结构中的输入直接写入复制的动态缓冲区
此外，当烘焙的 Ghost 实体包含由 `IInputCommandData` 组成的结构时，系统会自动为实体添加底层 `ICommandData` 动态缓冲区
但是，瘦客户端不会创建 Ghost 实体，因此无法使用此烘焙过程

瘦客户端也支持 `ICommandData`，详见[命令流](command-stream.md)，但仍需执行下述与 `IInputComponentData` 相同的瘦客户端接入工作

因此，若要支持从瘦客户端发送输入，必须执行以下操作：

1. 创建一个实体，其中包含 `IInputCommandData`（或 `ICommandData`）组件，以及由代码生成的 `YourNamespace.YouCommandNameInputBufferData` 动态缓冲区。**IDE 可能会显示缺少程序集定义的错误，但实际可以正常工作**
1. 设置 `CommandTarget` 组件，使其指向该实体。因此，在带有 `[WorldSystemFilter(WorldSystemFilterFlags.ThinClientSimulation)]` 的系统中执行：
```c#
    var myDummyGhostCharacterControllerEntity = entityManager.CreateEntity(typeof(MyNamespace.MyInputComponent), typeof(InputBufferData<MyNamespace.MyInputComponent>));
    var myConnectionEntity = SystemAPI.GetSingletonEntity<NetworkId>();
    entityManager.SetComponentData(myConnectionEntity, new CommandTarget { targetEntity = myDummyGhostCharacterControllerEntity }); // 告诉 Netcode 包应为哪个实体发送输入
```
1. 在服务器上生成瘦客户端实际使用的角色控制器 Ghost 后，该 Ghost 会复制到所有正常客户端。此时**_只需_**为瘦客户端设置 `CommandTarget`，因为其他玩家 Ghost 通常都使用 `AutoCommandTarget`。如果**_没有_**使用 `AutoCommandTarget`，可能已经为所有客户端执行了这一操作
```c#
    entityManager.SetComponentData(thinClientConnectionEntity, new CommandTarget { targetEntity = thinClientsCharacterControllerGhostEntity });
```
