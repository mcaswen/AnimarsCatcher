# 关键类与扩展点

[返回架构总览](README.md)

本文不按类名做字典式罗列，而是沿着实际运行链路说明关键类为什么存在、彼此怎样配合，以及新增功能时应该接入哪里。

## 1. 进程角色与 World 创建

项目启动后，第一件事不是进入具体玩法，而是决定当前进程要承担什么网络角色。

- `NetworkRuntimeRole` 位于 `Netcode/Bootstrap/NetworkRunRole.cs`，负责在 Bootstrap 运行前判断当前是 Host、Client 还是 Dedicated Server
- `CustomBootstrap` 位于 `Netcode/Bootstrap/CustomBootstrap.cs`，根据进程角色创建 Client World、Server World 和可选 Thin Client World
- `NetworkWorldLocator` 位于 `Netcode/Tools/NetworkWorldLocator.cs`，为 MonoBehaviour 和 UI 提供查找 Client World 或 Server World 的统一入口
- `ServerNetCodeController` 把主线程发出的“开始监听”命令转换为 Server World 中的请求 Entity
- `ClientNetCodeConnector` 把“连接某个地址”转换为 Client World 中的连接请求

这几类构成 GameObject 流程进入 ECS/NetCode 的门面。业务 UI 应调用这些入口，不应自行遍历所有 World，也不应在多个面板里重复实现连接状态机。

## 2. 房间、开局和角色出生

房间阶段仍由 MonoBehaviour 面板负责流程编排，真正的连接状态和开局状态则由 NetCode World 维护。

Host 侧的主入口是 `HostRoomPanelController`。它依次启动服务器监听、本机 Client 回连、LAN 广播，并刷新房间 UI。Client 侧由 `ClientRoomPanelController` 负责 LAN 发现、连接、超时和玩家信息握手。

点击开始游戏后，关键类按以下顺序协作：

1. `HostStartMatchRequestSender` 从 Host 的 Client World 创建 `StartMatchRequestRpc`
2. `ServerStartMatchSystem` 接收请求，并用 `ServerMatchStartState` 记录当前开局阶段
3. Server 向每个连接发送目标场景，`ClientHandleStartMatchNotificationSystem` 收到后创建 `ClientSceneLoadRequest`
4. `ClientSendReadyForGameRpcSystem` 等待 Ghost 资源满足当前 Ready 条件，再发送 `ClientReadyForGameRpc`
5. `ServerGetConnectionAspect` 封装连接的 InGame 标记、出生去重和 `CommandTarget` 写入
6. `CharacterSpawnUtility` 负责实例化玩家角色并选择出生位置

这条链路的扩展点很明确：修改房间交互时从两个 Room Panel 入手；修改协议阶段时从 `ServerMatchStartState` 和对应 RPC System 入手；修改出生策略时集中调整 `CharacterSpawnUtility` 和出生点注册数据。

## 3. 玩家输入、预测和相机

玩家移动不是直接修改 Transform，而是把输入写成逐 Tick 的网络命令，再由 Client 和 Server 执行同一套 KCC 模拟。

- `ClientPlayerInputSystem` 每帧只采集一次键盘和鼠标快照，并写入 `PlayerInput`
- `InputCommand` 是发送给 NetCode 的 Tick 命令，承载移动、视角、缩放和按钮状态
- `ThirdPersonCharacterPredictedMoveSystem` 读取命令并生成预测角色控制数据
- `ThirdPersonCharacterAspect` 封装 KCC 更新需要的组件访问和移动处理
- `ClientEnsureCommandTargetSystem` 把本地连接、所属角色 Ghost、Player 控制实体和相机关联起来
- `ClientThirdPersonPlayerBuildCameraControlSystem` 把输入写入相机控制组件
- `MainCameraSystem` 最后把 ECS 相机姿态应用到 Unity Camera

当前同时保留 Fixed 和 Orbit 两套相机实现，两套移动命令构建系统没有按相机模式互斥。新增相机模式前，应先建立唯一的模式选择状态，让同一 Tick 只有一个系统能写 `InputCommand`。

## 4. Ani 指令、FSM 和移动

Ani 的服务端移动链路由“接收命令、解析目标、规划路径、生成移动意图、执行位移”五个阶段组成，具体实现由互斥的 Grid 或 Legacy 后端决定。

Grid 入口是 `ServerAniCommandIngressSystem`。它根据 `SourceConnection` 和 `GhostOwner` 验证成员所有权，检查目标 Entity 与坐标，再生成统一的 `AniSquadCommand`。Legacy 入口是 `ServerReceiveAniCommandRpcSystem`，它完成旧链路的权限检查后把目标和命令模式写入 `FsmVar` Blackboard。

FSM 本身分成三个步骤：

- `ServerFsmEvaluateSystem` 判断当前状态是否满足迁移条件
- `ServerFsmApplyTransitionSystem` 执行旧状态退出和新状态进入
- `ServerFsmTickSystem` 执行当前状态的持续行为

`FsmRegistry` 保存条件和动作函数指针。Blackboard Key 是跨系统契约，新增 Key 时必须同步检查定义、初始化、所有读写方，以及它是否真的需要参与 Ghost 同步。

FSM 和阵型数据最终汇入移动规划：

下面这些类型已经整体移动到 `Assets/Scripts/Benchmarks/LegacyNavMesh`。它们当前仍是正式场景正在使用的移动实现，目录迁移只用于固定重构前基线，不会自动禁用 System。

- `AniFormationManagementSystem` 维护队伍槽位和成员关系
- `AniMovementPlannerSystem` 把 FSM 命令与阵型状态转换为导航目标
- `ServerNavMeshPlannerSystem` 调用 Unity NavMesh 生成 `NavWaypoint` Buffer，并发布 `NavSteering`
- `NavFollowIntentSystem` 读取 Steering，生成 `AniMoveIntent.DesiredVelocity`；在 Server World 中还会推进路径点索引
- `AniPhysicsMoveSystem` 执行 Ani 的服务端权威位移

修改某个阶段时，应先确认上游写入的数据和下游读取时机，不要只在单个 System 内补临时状态。

Grid 后端已完成 Stage 1～5 自动验收，并在 Navigation R6 中通过算法复审和 32、64、128 Ani 双轮固定窗口基准。关键类型如下：

- `NavigationGridAuthoring` 配置世界 Bounds、Cell Size、地面与障碍 Layer、坡度、台阶和基准 Agent 尺寸
- `NavigationGridBakeUtility` 在编辑器中同步 Physics 后采样中心地面、基础 Agent 环形支撑和静态占用，再生成邻接、Clearance、Region 和稳定 Hash
- `NavigationGridBakeAsset` 保存可在 Inspector 中检查的 Cell 数据，并作为场景与运行时 Blob 之间的版本边界
- `NavigationGridBaker` 先复用完整新鲜度校验，再把可用资产转换为共享的 `NavigationGridBlob` 和 `NavigationGridReference`
- `NavigationGridAuthoringEditor` 与 `NavigationGridInspectorWindow` 提供烘焙、过期校验、颜色图例和单 Cell 检查
- `NavigationGridVisualizationRenderer` 把烘焙高度上的 Cell 合并为缓存 Mesh，支持可行走、Clearance、Region、坡度、地形成本和指定 Agent 可占用性覆盖层
- `NavigationGridBuildValidator` 在登记了 Grid Authoring 的构建场景中拒绝缺失或过期资产
- `NavigationPathRequest`、`NavigationPathState` 和 `NavigationPathWaypoint` 构成路径服务的 ECS 契约。调用方通过递增 `Version` 区分新旧请求，并只消费同版本完成结果
- `NavigationGridQuery`、`NavigationGridTraversal` 和 `NavigationGridCost` 保存世界坐标转换、端点投影、通行与成本规则，供 A*、HPA、Flow Field 和 Squad 共同使用
- `NavigationGridPathfinder`、`NavigationAStarOpenSet`、`NavigationAStarScratch` 和 `NavigationPathSmoothing` 分别负责确定性 A*、堆、搜索工作区、路径回溯与平滑，不访问 EntityManager 或主线程 API
- `NavigationGridPathfindingJob` 在单个 Burst Job 内处理一个稳定排序后的请求批次，多个请求顺序复用 G Cost、Parent、Heap 和 Generation 数组
- `ServerNavigationGridPathfindingSystem` 只在 Server 或 Local World 运行。主线程负责收集请求和提交已完成结果，搜索期间不调用 `Complete`，下一 Tick 只在 Handle 已完成时写回 Buffer
- `NavigationGridStageTwoValidation` 使用合成 Grid 验证投影、Region、穿角、Clearance、Terrain Cost、确定性、失败状态和异步 ECS 写回
- `NavigationGridHierarchyBuilder` 在烘焙期生成 Cluster、Portal、Portal Node 与抽象边，并保存最小 Clearance 和静态成本
- `NavigationGridCorridorSolver` 负责 HPA Corridor，`NavigationIntegrationFieldSolver` 负责局部 Integration Cost，`NavigationFlowFieldSolver` 与 `NavigationFlowFieldCache` 负责方向场编排和缓存生命周期
- `NavigationGridFlowFieldJob` 异步生成 Corridor、Integration Cost 和离散下降方向，动态 Overlay 版本变化时只失效相关数据
- `ServerNavigationGridFlowFieldSystem` 在 Server 或 Local World 异步处理阶段三请求，主线程不执行同步搜索
- `NavigationDynamicOverlaySystem` 消费动态 Cell 差量，维护阻挡计数、额外成本、Clearance 修正与 Cluster 版本
- `AniSquadLifecycleSystem`、`AniSquadTargetResolveSystem` 和 `AniSquadPathRequestSystem` 把一条合法指令维护为一个 Squad 路径上下文
- `AniSquadAnchorAdvanceSystem` 推进共享 Anchor，`AniAdaptiveFormationSystem` 根据前方通行宽度动态改变矩形阵型列数
- `AniFormationLayoutSystem` 生成中心对称的矩形或单列槽位，`AniFormationAssignmentSystem` 使用确定性的最小总代价匹配分配成员
- `AniPreferredVelocitySystem` 生成受速度和加速度限制的移动意图，`AniMovementCommitSystem` 是 Grid 后端唯一的 Ani `LocalTransform` 写入者，`AniMovementProgressSystem` 负责终态判定
- `NavigationGridStageOneValidation` 至 `NavigationGridStageFiveValidation` 覆盖烘焙、路径、Flow Field、Squad、自适应阵型和动态 Overlay；R6 额外覆盖动态 Corridor、Bellman 后继、缓存换代和终态稳定性
- `ServerNavigationGridBenchmarkSystem`、`ServerNavigationGridMovementBenchmarkSystem` 与 `ServerNavigationGridScaleInputBenchmarkSystem` 分别提供 Path/Field、严格阵型历史基线和阶段六规模输入工作负载，固定窗口结果写入 `BenchmarkResults/GridNavigation`

固定烘焙验收场景位于 `Assets/Scenes/Benchmarks/SCN_GridBakeStage1.unity`，对应资产位于 `Assets/SO/Navigation/SO_NavigationGrid_SCN_GridBakeStage1.asset`。算法验收使用运行时相同 Blob 与 Job 构造合成地图，不依赖场景对象。后端互斥由启动配置与 Guard 保证；未指定参数时仍使用 Legacy，Grid 通过 `-movement-backend=grid` 显式启用。阶段六已经完成 6A.0 规模输入、确定性 Hash、报告格式与预算基线；MovementOrder、正式 Cohort、目标区域、共享 Field、空间哈希、ORCA 和选择性世界碰撞 System 尚未实现。阶段七资源迁移和正式后端切换同样尚未实现。

## 5. 战斗和生命值

战斗链路横跨 Server ECS、Ghost 状态、Client GameObject 动画和命中 RPC，是当前耦合最深的区域之一。

服务端部分：

- `ServerAniAttackSenseSystem` 按距离和目标优先级选择攻击对象
- `ServerAniAttackFireSystem` 处理冷却，生成 `ShotId`，并冻结本次 `AniPendingAttack`
- `ServerApplyMeleeHitRpcSystem` 和 `ServerApplyRangedHitRpcSystem` 接收客户端回报的候选命中
- `ServerApplyDamageSystem` 读取当前 `DamageEvent` Buffer，累加其中已有元素并更新 `Health`
- `ServerAniDeathSystem` 销毁生命值耗尽且未被排除的实体；当前查询范围实际上不只包含 Ani

客户端表现部分：

- `BlasterAniAttackView` 和 `PickerAniAttackView` 负责动画、IK、射线与动画事件
- `AniAttackEventBridge` 和 `AniHitBridge` 把 GameObject 事件排队给 Client ECS，再由 RPC System 发送给服务器

这里的核心扩展原则是：客户端只能提交候选信息，最终攻击者、目标、距离、阵营、冷却和伤害必须由服务器重新确认。当前实现仍有未闭合部分，详见 [已知边界与演进方向](07_KnownRisks.md)。

## 6. 资源和经济

资源系统分为资源生成、资源破坏、Picker 分配、搬运交付和玩家资源统计几个阶段。

- `ServerResourceRespawnSystem` 根据刷新区域、波次和预算生成资源
- `ServerFragileCrystalDeathSystem` 把被破坏的脆弱水晶转换为可搬运掉落物
- `ServerAssignSelectedAniToResourceSystem` 从当前玩家已选中的 Picker 中分配搬运者
- Legacy `ServerResourceCarrySetupSystem` 管理旧 NavMesh 后端的站位、到达状态和开始搬运条件
- Legacy `ServerResourceCarryMoveSystem` 移动资源，完成交付并释放 Picker
- `ServerPlayerResourceApplyDeltaSystem` 消费资源事件 Hub 中的增量
- `ServerPlayerAniCountUpdateSystem` 按 `GhostOwner` 重建每个玩家的 Ani 数量统计

UI 通过 `ResourceStateReader` 读取状态。本地玩家资源来自 Client World；比赛时间当前却直接查询 Server World，所以纯 Client 无法正常读取该值。这不是推荐模式，而是现状和待修复边界。

## 7. Hybrid View 与 UI 桥

项目中的网络 Entity 不直接携带完整 GameObject 表现，而是由 Client Presentation System 根据托管 Prefab 引用创建 View。

角色 View 的典型链路是：

1. `EntityViewAuthoring` 把本地 View Prefab 烘焙为托管组件
2. `ClientSpawnEntityViewSystem` 在 Client World 中实例化 GameObject
3. `EntityViewFollower.Bind` 接收目标 Entity 和所属 `EntityManager`
4. `EntityViewFollower` 逐帧同步姿态、动画速度和生命周期

其他桥接沿用同一思路：

- `ClientSpawnHealthBarViewSystem` 根据 ECS `Health` 创建 `HealthBarView`
- `ClientAniSelectionUIAttachSystem` 把场景中的 Camera、Canvas 和 RectTransform 注入 Client ECS
- `LobbyClientJoinedNotification`、`MatchStartedNotification` 和 `ClientSceneLoadRequest` 从 Networking 发布生命周期通知
- `NetworkPresentationBridgeSystem` 把通知 Entity 转换为 Mono 加载流程和 `NetworkPresentationEvents`
- `UIInputEvents`、`AniSelectionEvents` 和 `ResourceRequestEvents` 用独立 static UnityEvent 承载三类本地请求
- `PresentationEventBus` 负责主菜单内部的 MonoBehaviour 事件流
- `GlobalLoadingUI` 管理持久加载遮罩和异步切换场景
- `BattleIntroCinematic` 管理 Cinemachine 开场、HUD 显隐和输入占用
- `Presentation.Match.MatchResultUIBridge` 把 Client ECS 收到的胜负结果交给结算面板

新增 View 时，应沿用“Authoring 提供引用、Client System 实例化、Bind 显式注入 World、Spawned Tag 去重、View 自行检测生命周期”的流程。

## 8. 常见扩展方式

### 8.1 新增网络实体

1. 先定义运行时 Component，并判断哪些字段确实需要网络同步
2. 用 Authoring/Baker 写入静态配置，不在运行时到处读取 MonoBehaviour
3. 在 Network Prefab 中保持稳定 Archetype，能力组件尽量预烘焙
4. 需要 GameObject 表现时，使用托管 Prefab 引用和 Client Presentation System
5. 明确谁创建、谁写权威状态、谁读取，以及谁负责销毁

### 8.2 新增 Client 到 Server 的请求

1. RPC 只携带玩家意图和稳定标识符，不携带最终权威结果
2. Server 从 `SourceConnection` 获取发送者身份
3. 重新验证所有权、目标类型、对局阶段、距离、成本和发送频率
4. 用 ECB 写入结果，并在消费后销毁 RPC Entity
5. 持续结果用 Ghost 返回，一次性结果用 Server 到 Client RPC 返回

### 8.3 新增 Hybrid View

1. 在 Entity 上烘焙 View Prefab 的托管配置
2. 只允许 Client Presentation System 实例化表现对象
3. 显式注入目标 `EntityManager`，不要依赖 `World.DefaultGameObjectInjectionWorld` 猜测 World
4. 用 Spawned Tag 防止重复生成
5. View 在 Entity 或 World 失效时主动清理自己

## 9. 修改前要联动检查什么

- 修改 Ghost Component 字段时，同时检查 Ghost Prefab 配置、序列化兼容、预测或插值行为和带宽
- 修改 RPC 字段时，同时检查发送方、接收方、权限校验、长度上限和协议版本
- 修改 Authoring 字段时，同时检查 Prefab/Scene Override、Baker、默认值和旧资源迁移
- 调整 System Group 或更新顺序时，同时检查上游数据新鲜度、ECB 回放时机和可能的一帧延迟
- 修改 Scene/SubScene 时，同时检查 Build Settings、AutoLoad、注册表和 Client Ready 条件
- 修改 View Prefab 时，同时检查托管配置、Spawned Tag、绑定组件和销毁路径
- 修改阵营规则时，同时检查出生、选择、攻击、血条颜色和胜负结算
