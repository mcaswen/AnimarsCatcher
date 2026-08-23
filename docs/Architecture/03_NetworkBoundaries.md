# 客户端与服务端边界

[返回架构总览](README.md)

## 1. World 如何创建

`CustomBootstrap` 是 NetCode World 的创建入口。项目将 `AutoConnectPort` 设为 `0`，因此不会依赖 NetCode 自动连接，监听和连接都由大厅或调试流程显式触发。

不同运行方式会创建不同 World：

- Editor Host 创建 Server World 和 Client World，并可按 Multiplayer PlayMode 配置附加 Thin Client
- Editor Client 只创建 Client World，也可以附加 Thin Client
- Editor Server 只创建 Server World
- Player 使用 `-host` 时同时创建 Server World 和 Client World
- Player 使用 `-client` 时只创建 Client World；无参数启动当前也默认按 Client 处理
- Dedicated Server 使用 `-dedicated`，只创建 Server World

`NetworkWorldLocator` 为 MonoBehaviour 和 UI 提供查找 Client World、Server World 的入口。ECS System 自身应通过 `WorldSystemFilter` 明确运行侧，不应依赖静态查找来决定自己承担客户端还是服务器职责。

## 2. 网络数据采用哪种方式传输

项目根据数据的生命周期使用四种主要机制。

### 2.1 Tick Command

`InputCommand : ICommandData` 用于持续输入。客户端每个 Network Tick 写入移动命令，NetCode 保存命令历史，并支持预测和回滚。它适合方向、跳跃等需要连续模拟的数据，不适合开局或选择这类一次性业务请求。

### 2.2 RPC

RPC 用于只需要消费一次的请求、握手和通知。例如进入游戏、选择 Ani、下达移动命令、回报命中候选以及广播比赛结束。

Client 到 Server 的 RPC 只是“请求”，不能直接视为已经验证的结果。服务器必须从 `ReceiveRpcCommandRequest.SourceConnection` 开始检查发送者身份、所有权和业务条件。

### 2.3 Ghost Snapshot

Ghost Snapshot 用于持续复制服务器权威状态，例如 `Health`、`Camp`、玩家资源和攻击触发状态。服务器写入真实值，客户端读取快照并进行预测校正、插值或表现更新。

给 Component 添加 `GhostComponent` 或 `GhostField` 标注，并不会自动把普通场景 Entity 变成 Ghost。这个 Entity 还必须进入实际 Ghost Prefab 或预生成 Ghost 链路。

### 2.4 进程内桥接

生命周期通知 Entity、`NetworkPresentationBridgeSystem`、按职责拆分的表现事件、攻击事件队列以及 managed `IComponentData` 用于同一进程内 ECS World 与 GameObject UI/View 之间的交互。Networking 只发布短生命周期通知，Mono 桥接负责加载界面和 UnityEvent 适配。这类桥接不经过网络，也不能替代客户端和服务器之间的正式同步。

## 3. 客户端和服务器分别负责什么

### 3.1 客户端负责采集意图与表现

客户端可以：

- 采集键盘、鼠标等设备输入
- 执行屏幕框选、射线检测和本地候选目标计算
- 对自己拥有的玩家角色执行移动预测
- 发送 Ani 选择、移动、生成和命中候选等请求
- 根据 Ghost 状态播放动画、特效、音频并更新 HUD
- 创建 Entity View、血条、选择光圈和其他 GameObject 表现

客户端不应直接决定伤害、资源到账、Ani 权威状态、基地败北或最终胜方。即使本地已经计算出一个候选结果，也必须由服务器复核。

### 3.2 服务器负责身份、规则与最终状态

服务器负责：

- 分配 `NetworkId`、阵营和出生点
- 创建玩家角色、Ani、基地和资源
- 运行玩家角色的权威 KCC 模拟，并通过快照校正客户端预测
- 复核 Ani 的所有权，并由当前启用的 Grid 或 Legacy 后端更新 Squad/FSM、阵型、寻路和移动意图
- 决定 Ani 感知目标、攻击冷却和 `ShotId`
- 校验动画命中候选，写入 `DamageEvent` 并结算 `Health` 和死亡
- 处理资源刷新、搬运、交付和玩家资源变化
- 判断基地败北和比赛结果，并向所有客户端广播

服务器不创建客户端 HUD、相机、音频或 GameObject View。

### 3.3 双方共同模拟的部分

玩家角色使用 Owner-predicted 模式。拥有者客户端和服务器执行相同的 KCC 移动逻辑，客户端获得即时响应，服务器保留最终权威并通过 Snapshot 校正偏差。

Ani、基地和资源主要采用服务器权威、客户端插值的方式。客户端不运行这些对象的权威 FSM、资源分配或伤害结算。

## 4. 当前 RPC 链路

### 4.1 大厅与开局

- `LobbyIntroRequestRpc` 从 Client 发往 Server，由 `ServerReceiveLobbyIntroRpcSystem` 接收，用于提交大厅显示名称
- `StartMatchRequestRpc` 从 Host Client 发往 Server，由 `ServerStartMatchSystem` 接收，用于请求开始目标场景
- `StartMatchNotificationRpc` 从 Server 发往 Client，由 `ClientHandleStartMatchNotificationSystem` 接收，用于广播服务器决定的目标场景
- `ClientReadyForGameRpc` 从 Client 发往 Server，由 `ServerHandleReadyForGameRpcSystem` 接收，表示客户端场景和 Ghost 资源已经就绪
- `DebugEnterGameRpc` 从 Client 发往 Server，由 `ServerHandleDebugEnterGameRpcSystem` 接收，仅用于 Editor 调试直入游戏

### 4.2 Ani 生成与指挥

- `SpawnAniRequestRpc` 从 Client 发往 Server，由 `ServerSpawnAnisSystem` 接收，用于请求生成两类 Ani
- `AniSelectionChunkRpc` 从 Client 发往 Server，以最多 120 个 GhostId 为一块携带版本、块序号、成员计数和 Hash；`ServerAniSelectionSetSystem` 只有在全部分块、最终 Hash 与所有权都通过校验后才发布选择集
- `AniSelectionAckRpc` 从 Server 返回 Client，客户端只会用已确认的版本发送后续移动命令；空选择同样发布新版本，用于明确取消旧选择
- `AniCommandRpc` 从 Client 发往 Server，只携带目标、选择集版本和 Hash。Grid 后端由 `ServerAniCommandIngressSystem` 生成 `AniMovementOrder`，再由服务器拆成有界 Cohort；Legacy 后端读取同一选择集后更新旧 Blackboard，两个入口通过后端 Tag 互斥

选择集成员在服务器按 GhostId 排序并去重。内容一致的重复块按幂等重传处理，内容冲突的重复块、重复成员、越权成员、过期版本和超时未完成版本都会被拒绝。玩家连接失效时，对应未完成组装和已发布选择集都会清理。

### 4.3 战斗、资源与比赛结果

- `MeleeHitRpc` 从 Client 发往 Server，由 `ServerApplyMeleeHitRpcSystem` 接收，表示近战动画产生了命中候选
- `RangedHitRpc` 从 Client 发往 Server，由 `ServerApplyRangedHitRpcSystem` 接收，表示远程射线产生了命中候选
- `DebugAdjustResourceRpc` 从 Client 发往 Server，由资源调试系统接收，用于请求资源增量。它属于需要重点限制的调试入口
- `MatchResultRpc` 从 Server 发往 Client，由 `ClientHandleMatchResultRpcSystem` 接收，用于通知服务器判定的胜方

RPC Entity 在消费后必须销毁，避免同一请求被重复处理。

## 5. Ghost 与预测的具体边界

### 5.1 玩家角色

玩家角色的 Owner-predicted 链路如下：

1. Server 创建角色并写入 `GhostOwner.NetworkId`
2. Server 将对应连接的 `CommandTarget` 指向这个角色
3. Client 根据本地 `NetworkId` 找到属于自己的 `PredictedGhost`
4. Client 在 `GhostInputSystemGroup` 中把 `InputCommand` 写入命令缓冲
5. Client 和 Server 执行相同的 KCC 更新
6. Server Snapshot 校正客户端与权威状态之间的偏差

### 5.2 Ani、基地与资源

这些对象主要由服务器执行规则并同步状态。客户端接收位置和状态后，负责插值、朝向、动画和 View 更新，不执行权威 AI、战斗和经济结算。

主要同步内容包括：

- 身份与关系，例如 `GhostOwner`、`Camp`、选择和入队 Enable Bit
- 生存状态，例如 `Health`
- 玩家经济状态，例如 `PlayerResourceState`
- 表现触发，例如 `AniAttackFireRequest.ShotId`
- 资源交互状态，例如 `PickableResource`、`ResourceCarryingTag` 和 `AniCommandLockedTag`

有两项状态需要特别说明：

- `GameResult` 当前是 Server World 中的普通 Entity，并不是客户端可读取的 Ghost。客户端通过 `MatchResultRpc` 接收最终结果
- `GlobalGameResourceState` 虽然使用了 Ghost 相关标注，但当前场景 Entity 没有形成实际 Ghost 同步链路，因此纯 Client 无法直接读取服务器比赛时间

## 6. 服务端校验顺序

任何 Client 请求进入 Server 后，都应按以下顺序验证：

1. 确认 `SourceConnection` 存在，并具有有效 `NetworkId`
2. 确认连接已经进入正确阶段，例如具有 `NetworkStreamInGame`，并且当前开局状态允许该请求
3. 确认请求控制的 Entity 存在，且 `GhostOwner.NetworkId` 等于发送者
4. 确认目标 Entity 存在，并具有业务要求的 Tag 和 Component
5. 校验阵营、距离、当前状态、冷却、`ShotId` 和资源成本
6. 校验请求数量以及 FixedList 长度没有超过业务上限
7. 写入权威结果，并销毁 RPC Entity

当前各条链路的校验覆盖并不完全一致，具体差异记录在 [已知边界与演进方向](07_KnownRisks.md)。

## 7. ECS 与 MonoBehaviour 如何交互

客户端表现层通过以下桥接访问 ECS：

- `LobbyClientJoinedNotification`、`MatchStartedNotification` 和 `ClientSceneLoadRequest` 从 Networking 发布生命周期通知，不引用具体 UI
- `NetworkPresentationBridgeSystem` 消费通知 Entity，把网络状态适配为加载界面和 `NetworkPresentationEvents`
- `UIInputEvents`、`AniSelectionEvents` 和 `ResourceRequestEvents` 分别处理输入锁、选择模式和资源调试请求
- `PresentationEventBus` 负责主菜单和房间流程中的 MonoBehaviour 之间通信
- `NetworkWorldLocator` 让 MonoBehaviour 查找当前进程中的 Client World 或 Server World
- `ResourceStateReader` 直接查询 World。本地玩家资源从 Client World 读取，但比赛时间目前直接读取 Server World
- `AniAttackEventBridge` 和 `AniHitBridge` 把动画事件与射线命中候选从 View 传入 Client ECS
- `EntityViewConfig`、`HealthBarViewConfig` 等 managed `IComponentData` 把 Prefab 或 View 引用提供给客户端表现 System

不同 World 之间不能共享 `EntityManager`、Entity 或可变 NativeContainer。跨层桥接数据至少要包含明确的 `NetworkId`；如果同一进程中同时存在 Client World 和 Server World，还需要明确数据来自哪个 World。

当前 `TryGetGlobalGameResourceState` 会直接查找 Server World，所以只有 Host 进程能够稳定读取服务器维护的比赛时间。纯 Client 进程没有 Server World。后续需要把比赛时间纳入实际 Ghost 同步链路，或者通过专门的服务器消息同步。
