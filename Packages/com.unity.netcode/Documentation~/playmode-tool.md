# PlayMode Tool 窗口

使用 __PlayMode Tools__ 窗口，菜单路径为 **Window** > **Multiplayer** > **PlayMode Tools**，可以执行以下操作：

- 选择进入 Play 模式时 Netcode for Entities 启动流程的行为，前提是该流程已启用。它控制 `ClientServerBootstrap` 创建客户端 World、服务器 World 还是两者都创建，以及是否自动连接
- 启用和配置[网络模拟器](network-connection.md#network-simulator)
- 配置要使用的[瘦客户端](client-server-worlds.md#thin-clients)数量
- 修改当前日志级别，并控制 Unity 是否创建数据包转储
- 进入 Play 模式后，查看、控制和调试 Netcode for Entities 客户端与服务器 World 及其 Transport 数据
- 显示全部 Ghost 的包围盒 Gizmo

<img src="images/playmode-tool.png" width="600" alt="PlayMode Tool"/>

<a id="properties"></a>
## 属性

| 属性 | 说明 |
|------|------|
| __PlayMode Type__ | 决定进入 Play 模式时 Netcode for Entities 启动流程的行为，前提是该流程已启用。__Client__ 只生成客户端 World，__Server__ 只生成服务器 World，__Client & Server__ 各生成一个 |
| __Simulate Dedicated Server__ | 指定 Netcode for Entities 烘焙 SubScene 的 ServerWorld 版本时模拟的构建环境。例如，客户端托管的游戏服务器可能包含 Dedicated Game Server（DGS）构建中不可用的额外程序集；取消该开关后，这些程序集中的类型会出现在 ServerWorld 实体上。只有在 Project Settings 中启用 Client Hosted Builds 时才显示此选项，因为客户端不支持托管游戏服务器时，系统默认模拟 DGS |
| __Num Thin Clients__ | 设置包在编辑器内生成并自动维护的瘦客户端数量。可以使用它们测试多人 PvP 交互。瘦客户端没有表现层，也不会生成从服务器收到的 Ghost，但可以生成虚拟输入，并在服务器上模拟真实负载 |

> [!NOTE]
> 此窗口保持打开时，会在运行时严格维持目标 _Num Thin Clients_ 数量。如果自行生成瘦客户端并使总数超过该值，多出的瘦客户端会被销毁。这是一个已知问题

<a id="emulate-client-network-conditions"></a>
### 模拟客户端网络条件

在 Unity 编辑器中运行游戏时，可以使用网络模拟器复现特定网络条件

启用网络模拟后，可以通过以下方式设置数据包延迟和丢包：

- 手动设置数据包延迟和丢弃值
- 选择预设，例如 4G 或 Broadband

应经常在启用模拟器的情况下测试玩法，更准确地了解真实网络延迟对玩法质量的影响。玩法测试还会展示客户端预测回滚和重新模拟逻辑的性能成本。例如，Ping 越高，客户端需要执行的预测 Tick 越多，消耗的客户端 CPU 资源也越多

若要手动指定网络条件，请在以下字段输入自定义值：

- **RTT Delay**
- **RTT Jitter**
- **Packet Drop**

如果使用 **Packet View**，请在以下字段输入自定义值：

- **Packet Delay**
- **Packet Jitter**
- **Packet Drop**

Unity 通过 Unity Transport Pipeline Stage 执行网络模拟。该 Stage 只添加到客户端驱动，因此设置会同时应用于传入和传出数据包。若要查看对 Ping 的综合影响，请打开下拉菜单并选择 __Ping View__

| 属性 | 说明 |
|------|------|
| __RTT Delay (ms)__ | 模拟往返时间。该属性会分别延迟传入和传出数据包，二者延迟之和等于指定毫秒值 |
| __RTT Jitter (ms)__ | 在延迟上加减随机值，使实际延迟位于设定延迟加减抖动值的区间内。例如，__RTT Delay__ 为 45、__RTT Jitter__ 为 5 时，结果是 40 到 50 之间的随机值 |
| __Packet Drop (%)__ | 模拟部分数据包无法抵达的不良连接。指定百分比后，Netcode for Entities 会丢弃收到数据包总数中的对应比例。例如设为 5 时，会丢弃全部传入和传出数据包的 5% |
| __Packet Fuzz (%)__ | 模拟与安全有关的中间人攻击，恶意客户端通过故意序列化错误数据尝试使服务器或其他客户端崩溃 |
| __Auto Connect Address (Client only)__ | 指定客户端连接的服务器地址。仅当 __PlayMode Type__ 为 __Client__ 时显示。如果不使用自动连接，用户代码需要通过 `ClientServerBootstrap.IsEditorInputtedAddressValidForConnect` 读取该值，并手动连接到输出的 `NetworkEndpoint` |
| __Auto Connect Port (Client only)__ | 覆盖或指定服务器监听和客户端连接所使用的端口 |

> [!NOTE]
> 启用网络模拟时，Unity 会强制 Unity Transport 使用完整 UDP Socket 网络接口。否则，如果客户端与服务器 World 位于同一进程，Unity 会使用 IPC 连接。参阅 [DefaultDriverConstructor](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.IPCAndSocketDriverConstructor.html)

> [!NOTE]
> 仅客户端模式下自动连接客户端与服务器时，Unity 使用 `AutoConnectAddress` 和 `AutoConnectPort`，并覆盖 [ClientServerBootstrap](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerBootstrap.html) 中设置的值。但是，当 Bootstrap 将 `AutoConnectPort` 设为 0 时，Unity 会忽略这些字段。可以使用 PlayMode Tools 窗口中的 __Connect__ 按钮，强制连接目标 `AutoConnectAddress` 和 `AutoConnectPort`

<a id="visualize-bounding-boxes-on-gameobjects"></a>
### 显示 GameObject 包围盒

使用 Entities Graphics 的实体会自动绘制包围盒。若要为不使用 Entities Graphics 的对象绘制包围盒，请向 GameObject 对应的实体添加 `GhostDebugMeshBounds` 组件。可以调用 `Initialize` 辅助方法完成设置

示例请参阅 `GhostPresentationGameObjectEntityOwner`

<img src="images/DebugBoundingBox.png" width="600" alt="预测与服务器调试包围盒"/>

<a id="visualize-importance-of-ghosts"></a>
### 显示 Ghost 重要度

使用 [Ghost 重要度](optimization/optimize-ghosts.md#importance-scaling)时，可以在 PlayMode Tool 窗口中启用 Importance Visualizer，在 Scene 和 Game 视图中显示 Ghost 重要度

| 属性 | 说明 |
|------|------|
| __Connection entity__ | 选择需要显示重要度的连接 |
| __Draw entity mode__ | 选择重要度显示模式。**Per entity importance heatmap** 按实体重要度为每个实体着色；**Per chunk** 将实体按 Chunk 分组，并根据 Chunk 重要度着色 |
| __Tile draw mode__ | 选择是否绘制网格以及在哪个轴平面绘制。网格基于 [GhostDistanceData](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostDistanceData.html)，必须存在该数据才能工作 |
| __Heatmap Gradient__ | 自定义热力图使用的渐变 |
| __Render Distance__ | 要渲染的 Tile 数量，从 `(0, 0, 0)` 向外扩展 |

重要度可视化数据来自服务器 World。系统会在每个 Ghost 的位置与该 Ghost 所在 Chunk 的第一个实体之间绘制连线；Chunk 中第一个实体是任意的，没有特殊含义。使用 Per Entity 模式时，每条线根据渐变着色，高优先级使用第一个颜色，默认绿色；低优先级使用最后一个颜色，默认红色。使用 Per Chunk 模式时，同一 Chunk 中全部 Ghost 的连线颜色相同

Tile 绘制模式只适用于内置的[基于距离的重要度缩放](optimization/optimize-ghosts.md#distance-based-importance)，其网格基于 [GhostDistanceData](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostDistanceData.html)。如果未使用基于距离的重要度缩放，网格模式不适用

下图展示 Asteroids 示例项目使用 Per Entity Importance Heatmap 模式的结果，并在 XZ 平面显示网格。每颗小行星都有连线，多条线共同起始的位置表示这些 Ghost 属于同一个 Chunk。屏幕中央是玩家飞船，飞船位于一个 Tile 内；该 Tile 及其相邻 Tile 都是绿色。颜色是相对的，越绿表示 Chunk 优先级越高，距离更远的 Tile 优先级较低，因此更红。同一 Tile 内可能有多个 Chunk。还可以看到只有最近的 Tile 渲染了小行星，这是相关性而不是重要度导致的。该结果表明系统按预期工作：最近的小行星比远处小行星更新更频繁。如果使用不基于距离的自定义重要度缩放函数，连线可能相互交织，不会像本例这样清晰

<img src="images/importance-visualizer.png" width="600" alt="使用重要度可视化器的 Asteroids 示例"/>

> [!NOTE]
> Importance Visualizer 只在存在服务器 World 时工作，并且需要 [Entities Graphics](https://docs.unity3d.com/Packages/com.unity.entities.graphics@latest) 才能正确运行

<a id="initialize-the-network-emulator-from-the-command-line"></a>
## 从命令行初始化网络模拟器

使用命令行参数 `--loadNetworkSimulatorJsonFile [optionalJsonFilePath]` 加载现有 JSON 文件形式的 `SimulatorUtility.Parameters` 预设。找不到文件时，Unity 会抛出错误

也可以使用 `--createNetworkSimulatorJsonFile [optionalJsonFilePath]` 自动生成默认 JSON 文件。未指定名称时，默认文件名为 `NetworkSimulatorProfile.json`

传入任一参数都会启用模拟器配置，即使发生错误也是如此。如果文件未找到或未生成，则使用 `NetworkSimulatorSettings.DefaultSimulatorParameters`

> [!NOTE]
> 只有 Development 构建可以启用网络模拟

<a id="use-the-playmode-tool-window-with-multiplayer-play-mode"></a>
## 在 Multiplayer Play Mode 中使用 PlayMode Tool 窗口

若要在使用 Netcode for Entities 的项目中，通过 Multiplayer Play Mode 的 PlayMode Tool 窗口测试[虚拟 Player](https://docs-multiplayer.unity3d.com/mppm/current/virtual-players/)，请执行以下操作：

1. [安装 Multiplayer Play Mode 包](https://docs-multiplayer.unity3d.com/mppm/current/install/)
2. 打开 Multiplayer Play Mode 窗口：**Window** > **Multiplayer Play Mode**
3. [激活虚拟 Player](https://docs-multiplayer.unity3d.com/mppm/current/virtual-players/virtual-players-enable/)
4. 在虚拟 Player 的 Play Mode 窗口中打开 **Layout**，选择 **PlayMode Tool**
5. 设置 **Play Mode Type**，使该克隆作为 __Client__、__Server__ 或同时作为 __Client & Server__ 运行

>[!NOTE]
> 如果项目中安装了 [Dedicated Server 包](https://docs.unity3d.com/Packages/com.unity.dedicated-server@1.0/manual/index.html)，所选 [Multiplayer Role](https://docs.unity3d.com/Packages/com.unity.dedicated-server@1.0/manual/multiplayer-roles.html) 会覆盖 PlayMode Type
