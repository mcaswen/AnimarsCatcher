# 导航模块（Navigation）阶段六：万人群体移动、最优互惠避碰（ORCA）与世界碰撞执行计划

## 术语约定

本文先使用中文业务含义，再在括号中标出源码或行业术语。后文重复出现时可直接使用英文名称，代码类型名不因此改名。

- **导航网格（Grid）**：把可移动地面离散成规则格子的寻路数据
- **网格单元（Cell）**：导航网格中的最小查询单位
- **网格分块（Cluster）**：由一组相邻 Cell 组成的固定空间分块
- **分块通道（Portal）**：相邻 Cluster 之间可以通过的连接
- **导航走廊（Corridor）**：从起点 Cluster 到目标 Cluster 经过的分块与 Portal 序列
- **可通行余量（Clearance）**：Cell 或 Portal 距离最近障碍还剩多少可用空间
- **流向场（Flow Field）**：为 Corridor 内 Cell 保存朝向目标的局部移动方向
- **目标流向场（Goal Flow Field）**：以目标为中心反向构建、允许不同起点共享并按空间分块扩张的流向数据
- **动态覆盖层（Overlay）**：运行时障碍对 Grid 通行状态和成本产生的增量修改
- **分层寻路（HPA）**：先在 Cluster 和 Portal 组成的抽象图上找走廊，再计算局部路径
- **移动请求（MovementOrder、`AniMovementOrder`）**：玩家一次完整移动意图及其成员快照
- **导航分组（Cohort、`MovementCohort`）**：从同一移动请求中拆出的有界 Ani 分组，用于共享寻路和归约进度，不表示可见阵型
- **旧阵型分组（Squad）**：阶段四至阶段五使用的严格阵型对象，只保留为历史回归基线
- **移动配置（Agent Profile）**：决定 Ani 体型、速度和寻路能力的一组参数
- **共享存储区（Store）**：集中管理可被多个 Cohort 复用的寻路结果
- **共享记录（Record）与引用句柄（Handle）**：Record 持有实际结果 Buffer，Handle 让 Cohort 引用它而不复制数据
- **最优互惠避碰（ORCA）**：通过相互承担避让责任求解局部安全速度的算法
- **Morton 空间键（Morton Key）与稳定编号（StableId）**：用于把空间位置和成员身份转换成可重复排序依据

[返回架构总览](README.md)

- [目标架构：RTS 2.5D Grid 导航、群体移动与避碰](08_AdaptiveFormationNavigationPlan.md)
- [Legacy NavMesh 与 Grid 性能基准](09_GridMovementImplementationBenchmark.md)
- [Grid 移动实现阶段与验收标准](10_GridMovementStagesAndAcceptance.md)
- [Navigation R1～R6 执行与验收](15_NavigationArchitectureRefactorExecutionPlan.md)

> 状态：阶段 6A.0～6A.4 已完成功能实现；万人完整回放未通过性能门禁，下一步为阻断 6B 的 6A.5 目标流向场共享与性能修复
>
> 目标：在服务器 World（Server World）支持最多 10000 个同时参与导航的 Ani，并保留确定性输入、异步寻路、零托管分配和唯一 Transform 写入边界
>
> 已确认决策：正式 Grid 后端取消严格矩形或纵队阵型，不使用 ORCA 代替目标分布；采用导航分组共享寻路、目标区域分散、空间哈希、ORCA 和选择性世界碰撞
>
> 名称说明：本文的“阶段六”是 Grid 移动功能阶段，不是已经完成的 Navigation 架构重构 `R6`

## 1. 决策与范围

当前 Stage 4～5 实现已经证明 Grid、HPA Corridor、局部 Flow Field、动态 Overlay、Squad Anchor、严格阵型和唯一 Transform Commit 可以在 32、64、128 Ani 的固定回放中稳定运行。它是第六阶段的行为与性能基线，但不是万人方案的最终数据模型。

第六阶段采用以下决策：

1. 玩家仍可直接框选任意数量的 Ani，框选只决定命令成员，不再决定矩形、纵队或职责槽位
2. 一次大规模移动命令先形成 `MovementOrder`，再按空间位置、体型配置和路线入口拆成有上限的 `MovementCohort`
3. Cohort 是共享 Flow Field、请求状态和重规划预算的计算单元，不是画面上的固定队形
4. 单个 Ani 直接消费共享 Flow Direction，并在接近目标时转向自己的目标区域落点
5. ORCA 只修正动态单位之间的局部速度，不负责生成队形，也不允许所有单位争抢同一个坐标
6. 静态世界由 Grid 与 Clearance 提供主要约束，Unity Physics 只承担最终防穿透和必要的 Slide 修正
7. 所有导航结果仍由服务器决定；Grid、Flow Field、邻居列表和 ORCA 约束不进入 Ghost
8. 第六阶段不删除 Legacy，也不把 Grid 改成默认后端；正式切换仍属于阶段七

第六阶段只承诺 10000 Ani 的服务器导航内核。10000 个完整 GameObject View、Animator、血条、战斗感知和逐 Tick Ghost 快照属于表现、玩法和网络的独立规模门禁，不能用导航 Benchmark 的通过结果替代。

## 2. 当前源码基线与扩展原因

| 当前边界 | 源码事实 | 万人规模影响 |
|---|---|---|
| 命令到 Cohort | 正式 Grid 入口只生成 `MovementOrder`，再按 Agent Profile、起始 Cluster、Morton Key 和 StableId 切分 Cohort | 默认容量 64、硬上限 128，万人请求不会进入旧 Squad 或生成双份成员 Buffer |
| 目标分布 | 目标区域按可通行 Cell、体型容量和稳定成员顺序分配独立落点 | 正式 Pipeline 不再创建矩形列数、职责槽位或 Hungarian 成本矩阵 |
| 历史 Squad | Stage 4～5 的严格阵型 System 和 Benchmark 入口继续保留 | 只用于行为回归和旧报告对照，不消费正式 MovementOrder |
| Flow Field 调度 | 正式 Cohort 已使用共享 Store、最多 8 个并行工作区和确定性发布，但 6A.4 万人回放仍为 251 个 Cohort 的四轮请求构建 938 份 Field | 构建批次长期占满 Worker，Server Tick P95 达到 `3016.4815 ms`，必须在加入 ORCA 前修复 |
| Flow Field 所有权 | Record 与 Handle 已避免结果 Buffer 复制，但 Key 仍包含精确起点 Cell，万人回放共享命中为 0 | 相同目标因 Cohort 起点不同被拆成近千份重复结果，Store 只有共享机制而没有形成有效共享粒度 |
| 单位移动 | 正式 Cohort 的期望速度、位移提交、成员进度和请求进度已改为并行 Job，历史 Squad 的逐成员速度与位移提交也使用同一并行边界 | 万人移动内核已验证零托管分配，空间哈希、ORCA 和世界碰撞仍待 6B 实现 |
| 动态 Overlay | Path 或 Flow Job 读取 Overlay 时，`NavigationDynamicOverlaySystem` 延迟写入 | 持续寻路负载可能增加动态障碍生效延迟 |
| 拥挤处理 | 当前 Stage 4 Benchmark 明确不包含 ORCA、世界碰撞或受阻恢复 | 开阔地到达不能证明窄路、交叉和高密度场景可用 |
| 验收规模 | 6A.2 已验证万人 Cohort 切分与 32、64、128、512 自由移动到达，6A.4 已验证 10000 Ani 并行移动功能 | 万人完整回放虽然全部到达，但严重超出冻结预算；6A.5 性能修复通过前不得进入 6B |

这些限制要求先改规模模型，再实现 ORCA。如果直接在现有“一个大 Squad + 固定槽位”上完成旧版阶段六，之后仍需重做空间哈希分组、邻居语义、到达判定和 Field 所有权。

## 3. 目标运行时模型

### 3.1 总体链路

```text
客户端框选
    -> 服务器选择集版本
    -> MovementOrder
    -> 确定性拆分 MovementCohort
    -> 直达判定或起点 Cluster 通道解析
    -> 目标流向场分块归并与预算调度
    -> 共享 FlowFieldHandle
    -> Ani Flow 期望速度 + 目标区域吸引
    -> Native 空间哈希
    -> ORCA 安全速度
    -> 选择性 Collider Cast / Slide
    -> 唯一 AniMovementCommitSystem
    -> 到达、受阻与重规划反馈
```

### 3.2 移动请求（MovementOrder）

`AniMovementOrder` 表示玩家的一次完整意图，当前保存：

- 所有者与稳定命令序号
- MoveTo、Follow 或 Find 语义
- 目标位置或目标 Entity
- 停止范围
- 选择集版本、完整性 Hash 与唯一成员 Buffer
- 创建 Tick、取消版本、优先级和目标区域容量及影响范围

成员 Buffer 同时冻结最大速度、最大加速度、Agent 半径和 Agent Profile，Cohort 生命周期不再回读易变的玩法属性。

移动 RPC 不再重复携带全部选中 GhostId。客户端先以分块或差量方式更新服务器选择集，服务端确认成员数量、Hash 和版本完整后，移动命令只引用该版本。后续可以评估由服务器根据框选体积重建选择集，但它不能成为第六阶段的前置假设。

服务器的 GhostId 到 Entity 索引只在 Ani 数量、结构、GhostId 或拥有者变化时重新发布，稳定 Tick 复用同一排序快照，不再由选择与移动入口各自逐 Tick 重建。当前变更发布仍需排序全部索引项，高频 Spawn、Despawn 的尖峰继续纳入 6C 性能治理。

### 3.3 导航分组（MovementCohort）

`MovementCohort` 是寻路与重规划的最小共享单元。第一版使用可配置上限，默认 64 Ani，硬上限 128 Ani；最终数值以 512～10000 Benchmark 为准。

拆分过程必须稳定且接近 `O(N log N)`：

1. 按所有者、命令和 Agent 通行配置分组
2. 将单位当前位置转换成 Grid Cell 或 Cluster
3. 按 Cluster、Morton Key 和 StableId 排序
4. 每 64 个成员切成一个 Cohort，尾组不得重复或丢失成员
5. 直达可行的 Cohort 不请求 Field；需要绕障碍时，相同目标和通行配置的 Cohort 共享目标流向场，起点只决定通道与所需覆盖范围

Cohort 保存代表性起点、目标、成员范围、Field Handle、重规划状态和进度聚合。它不保存矩形列数、职责槽位或可见队形，也不要求成员保持相对顺序。

### 3.4 目标区域分散

ORCA 不能解决终点占位。如果所有 Ani 使用同一目标坐标，外围单位会持续挤向已满中心。因此第六阶段新增轻量目标区域分散，但不恢复传统阵型。

目标区域按以下规则生成：

1. 将命令目标投影到合法 Grid Cell
2. 从投影中心沿动态通行边做连通扩张，障碍另一侧或其他不连通 Region 的 Cell 不参与分配
3. 根据 Agent 半径计算 Cell 容量，阻挡或 Clearance 不足的 Cell 不参与分配
4. 可达 Cell 再按距离、通行成本、Clearance 和稳定 CellIndex 排序
5. 成员按起始角度、空间 Key 和 StableId 稳定排序，目标 Cell 使用相同方向顺序匹配
6. 分配只使用连通遍历和排序，不建立成员乘槽位的完整成本矩阵
7. 远距离 Flow 请求与目标区域分配共享同一个投影中心，近目标时再混合个人落点吸引
8. 单位进入自己的停止范围且速度稳定后释放目标争用，不再持续挤向中心

该结果可以形成不规则但稳定的自然群体。它只保证可达、不过度重叠和确定性，不保证矩形、圆形、职业前后排或视觉上的严格对齐。

### 3.5 共享流向场存储区（Flow Field Store）

Stage 4～5 的旧 Squad 仍独立拥有 Corridor 和 Flow Field Buffer。6A.3 已为正式 Cohort 增加服务器全局共享记录：

```text
NavigationFlowFieldKey
    ProjectedStartCell
    TargetCell
    RequiredClearance
    ClearancePenalty

NavigationFlowFieldHandle
    Record Entity
    RecordVersion
    RequestVersion

NavigationFlowFieldRecord
    Corridor / Portal / Waypoint Buffer
    Flow Field Buffer
    Corridor Overlay Signature
    RefCount / LastUsedTick / ByteSize
```

Store 由 Grid Data Hash 隔离，Grid 换代时整体清空；Record 的 Overlay 签名只覆盖实际 Corridor 经过的 Cluster。同一个 Key 在同一时刻最多存在一个构建任务，所有等待的 Cohort 在确定性发布阶段取得同一 Handle，不再把完整 Field 复制进各自 DynamicBuffer。

6A.4 万人回放证明当前 Key 中的精确投影起点会破坏实际共享：251 个 Cohort 的四轮请求产生 938 次唯一构建，`SharedFieldHitCount=0`。6A.5 必须把起点相关的 Corridor 或覆盖范围与目标相关的 Integration、Direction 数据拆开。目标流向场 Key 不再包含精确起点 Cell；不同起点通过起点 Cluster、目标 Cluster 和所需 Field Tile 绑定到同一目标 Record，缺少的覆盖分块按需扩张，不能重建已有目标数据。

缓存每 Tick 为有效 Record 建立哈希索引，并按明确的字节预算淘汰最久未使用且无引用的完整 Record，不再固定为 64 项后整代清空。动态 Overlay 继续按 Corridor 涉及的 Cluster 版本失效，不允许无关分块变化使所有 Field 重建。

### 3.6 单个 Ani 数据

阶段六目标组件职责如下，最终名称可以在实现提交中按项目命名规范收敛：

- `AniMovementCohortMembership`：Cohort Entity、成员稳定编号和 Agent Profile
- `AniGoalAssignment`：目标 Cell、目标位置、停止半径和目标版本
- `AniPreferredVelocity`：Flow 与目标区域共同生成的原始期望速度
- `AniAvoidedVelocity`：ORCA 求解后的安全速度
- `AniSafeDisplacement`：世界碰撞修正后的本 Tick 位移
- `AniMovementResult`：实际速度、到达状态和唯一提交计数
- `AniStuckState`：连续低速、无进展、碰撞失败和重规划冷却

现有 `AniSquadFormationState`、`AniFormationSlot`、`AniSlotTarget`、`AniAdaptiveFormationSystem`、`AniFormationLayoutSystem` 和 `AniFormationAssignmentSystem` 在 6A 迁移完成前保留为旧 Grid 行为基线；等自由移动链路通过 32、64、128 回归和 512 基础验收后，再从正式 Grid Pipeline 移除。历史 Benchmark 结果继续保留，不改写为新方案结果。

## 4. 目标系统链路（System Pipeline）

服务器运行顺序调整为：

1. `ServerAniSelectionSetSystem`：组装并校验分块或差量选择集
2. `ServerAniCommandIngressSystem`：校验目标与选择集版本并创建 MovementOrder
3. `AniMovementCohortPartitionSystem`：确定性拆分或复用 Cohort
4. `NavigationDynamicOverlaySnapshotSystem`：完成写缓冲并在安全 Tick 边界交换只读快照
5. `AniCohortTargetResolveSystem`：解析 MoveTo、Follow 和 Find 的当前目标
6. `AniGoalRegionAssignmentSystem`：投影共享终点，并只在命令、目标区域或成员版本变化时更新落点
7. `NavigationFlowFieldRequestCollectSystem`：先识别可安全直达的 Cohort，再按目标、通行配置和缺失覆盖分块归并请求
8. `NavigationFlowFieldBuildSystem`：使用可复用工作区并行解析 Corridor 或扩张目标流向场，不重复构建已有 Tile
9. `NavigationFlowFieldPublishSystem`：按目标 Record 和稳定 Tile 顺序提交结果与 Handle，丢弃过期版本
10. `AniNeighborGridBuildSystem`：构建 Native 空间哈希和稳定邻居切片
11. `AniPreferredVelocitySystem`：并行计算 Flow、目标区域和制动速度
12. `AniLocalAvoidanceSystem`：并行求解有限邻居 ORCA 约束
13. `AniWorldCollisionSystem`：只为需要最终防穿透的单位生成安全位移
14. `AniMovementCommitSystem`：唯一写入 Ani `LocalTransform`
15. `AniMovementProgressSystem`：并行生成成员结果并按 Cohort 归约到达与受阻状态
16. `AniRepathRequestSystem`：根据动态目标、Overlay、持续受阻和预算提交下一轮请求

Field 构建 Job 不能直接并发修改共享缓存。并行阶段只写每个请求自己的固定切片或 `NativeStream`，发布阶段再按稳定 Key 顺序写入全局 Store，保持所有权和确定性清晰。

## 5. 阶段 6A：万人规模基础

### 6A.0 基准测试（Benchmark）与预算基线

交付物：

- 在现有 Harness 增加 512、1000、2500、5000 和 10000 Ani 参数
- 区分 `StrictFormationBaseline`、`FreeCohortMovement`、`Avoidance` 和 `Collision` 工作负载
- 记录完整 Server Tick、各 System 主线程时间、Worker 时间、请求排队时延和 Native 内存
- 记录 Cohort 数、唯一 Field Key、构建数、共享命中数、缓存字节数和重规划数

退出条件：

- 32、64、128 旧结果仍可重放
- 新规模入口不会受正式 RPC FixedList 容量影响
- 相同输入的 Cohort 切分 Hash、目标区域 Hash 和请求 Key Hash 重复运行一致
- 在进入 6A.1 前写明目标 Server Tick 预算和各导航阶段预算，不使用单次平均帧率代替门禁

实现状态（2026-08-22）：**已完成**

- 统一 Harness 已登记 `32 / 64 / 128 / 512 / 1000 / 2500 / 5000 / 10000` 八档规模，并提供固定入口和 `-benchmark-agent-count` 通用入口
- `PathAndField` 与 `StrictFormationBaseline` 只允许 32、64、128，防止旧单 Squad 阵型与逐请求链路被误放大到万人
- 新增内部工作负载 `ScaleInputDeterminism`，使用 ECS `DynamicBuffer` 生成最多 10000 条成员输入，不经过正式 Movement RPC 或 `FixedList`
- `FreeCohortMovement`、`Avoidance` 和 `Collision` 已成为独立工作负载标识；对应实现落地前 Harness 会明确拒绝运行，不会输出伪造的性能结果
- 报告格式升级为 v5，阶段六报告保留完整 Server Tick、主线程分配、System 主线程/Worker、排队 Tick、可归属 Native 内存、Cohort、Field 和重规划字段；旧工作负载同时写明计时覆盖范围与可用性，未采集的指标不以 `0` 冒充已测结果
- `ScaleInputDeterminism` 只验收规模入口、报告结构和输入确定性，固定写出 `PerformanceGateEligible=false`，不能作为自由移动、ORCA、碰撞或真实 Field 性能结论

冻结预算使用 `Stage6A0-60Hz-v1`：

| 门禁 | P95 预算 |
|---|---:|
| 完整 Server Tick | 16.667 ms |
| 完整 Server Tick P99 | 20.000 ms |
| Navigation 主线程合计 | 8.000 ms |
| Navigation Worker 关键路径 | 8.000 ms |
| 请求排队 | 4 Tick |
| Navigation 可归属 Native 内存 | 512 MiB |

主线程 8 ms 按阶段拆分为：命令入口 0.25 ms、Cohort 切分 0.50 ms、Overlay 与目标解析 0.50 ms、Field 请求收集 0.50 ms、Field 构建与发布 0.75 ms、目标区域分配 0.75 ms、邻居网格 1.25 ms、期望速度 0.75 ms、ORCA 1.50 ms、世界碰撞 0.75 ms、提交与进度 0.50 ms。单项和总量都使用 P95 门禁，不能用多轮平均值抵消尖峰。

本次验收在 Unity `6000.2.7f2`、Dedicated Server、Null Device 下完成：

- 新增 6A.0 自动验收通过，并回归通过 Stage Zero 与 Stage Four
- 32 Ani `StrictFormationBaseline` 完整回放通过，32/32 到达、4/4 路径成功、主线程分配 P95 为 0 B
- 10000 Ani `ScaleInputDeterminism` 连续两轮均生成 10000 条输入、79 个 Cohort 和 720 个 Server Tick 样本，主线程分配 P95 均为 0 B
- 两轮 `CohortPartitionHash=7FA032DD69575255`、`GoalRegionHash=CE895C650B74FB03`、`RequestKeyHash=7AC21B7695215AC3` 完全一致

注意：上述万人样本没有运行导航内核，因此其约 0.85～1.08 ms Server Tick P95 只证明 Harness 容量和采样闭环，不证明万人移动满足预算。第一份可参与导航性能门禁的万人报告必须等 6A.2～6B.3 对应工作负载实现后生成。

### 6A.1 选择集与移动请求（MovementOrder）

交付物：

- 服务器玩家选择集 Entity、选择集版本、成员 Buffer 和完整性 Hash
- 分块或差量选择协议，以及丢包、重复块、过期版本和超时清理
- 只引用选择集版本的移动命令
- 增量 GhostId 索引和权限校验

退出条件：

- 10000 个成员可以完整框选、取消、替换并提交一条 MoveTo
- 重复、越权、过期和不完整选择集均被服务器拒绝
- 同一成员在一个 MovementOrder 中只出现一次
- 命令不因分块到达顺序不同产生不同成员顺序或 Hash

实现状态（2026-08-22）：**已完成**

- 新增 `ServerAniSelectionSet`、版本、完整性 Hash 和按 GhostId 升序排列的成员 Buffer；未完成版本使用独立组装 Entity，玩家连接失效时同步清理
- `AniSelectionChunkRpc` 每块最多携带 120 个 GhostId，支持 Replace、Add、Remove 和 Clear；块可乱序到达，内容一致的重复块按幂等重传忽略，内容冲突的重复块和重复成员会拒绝整个版本
- 服务端会核对来源连接、当前 `GhostOwner`、块数、载荷成员数、结果成员数和最终 Hash；更高版本会取消旧的未完成组装，过期版本直接拒绝，缺块版本在 180 个 Server Update 后清理
- `AniSelectionAckRpc` 只确认已经发布的版本；客户端在收到回执前保留世界点击，`AniCommandRpc` 只发送目标、选择集版本和 Hash，不再携带固定容量成员列表
- `ServerAniGhostIdIndexSystem` 统一向选择和命令链路发布排序索引，稳定 Tick 不刷新；Grid 与 Legacy 入口不再维护各自的逐 Tick GhostId HashMap
- Grid 入口生成 `AniMovementOrder` 与唯一成员快照；Legacy 入口从同一权威选择集读取成员，不改变旧 FSM 与 NavMesh 行为

专项验收在 Unity `6000.2.7f2` Batch Mode 下完成：

- 10000 Ani 选择集拆为 84 块，顺序与逆序到达都发布 10000 个唯一成员
- 两种到达顺序的 `SelectionHash` 与成员顺序 Hash 均为 `78681BD7C145FFE4`
- 完整覆盖万人框选、Clear 取消、Replace、Add、Remove、MoveTo、重复成员、冲突块、越权成员、过期版本和缺块超时
- `AniCommandRpc` 结构检查确认不再包含任何 `FixedList` 成员字段，一个 MovementOrder 中的 10000 个成员严格升序且各出现一次
- 6A.1 专项自动验收、6A.0 自动验收和 Stage Four 自动验收全部通过

### 6A.2 导航分组（Cohort）与自由目标区域

交付物：

- 稳定 Cohort 切分、生命周期和成员变更处理
- 目标区域生成、容量计算和稳定落点分配
- 移除正式 Pipeline 对矩形列数、职责槽位和 Hungarian 匹配的依赖
- 远距离 Flow 移动与近目标落点吸引的速度混合

退出条件：

- 任意规模不存在超过配置硬上限的 Cohort
- 成员不重复、不丢失，死亡、移除和新命令不会留下悬空归属
- 不存在 `N * N` 成本矩阵或随总成员数平方增长的持久内存
- 32、64、128 开阔地全部到达；512 Ani 不依赖 ORCA 也能完成无交叉基础场景

实现状态（2026-08-23）：**已完成**

- 正式 Grid 入口已移除旧 `AniSquadCommand` 和成员 Buffer 适配，Stage 4～5 Squad 只保留历史 Benchmark 与专项回归入口
- `AniMovementCohortPartitionSystem` 按 Agent Profile、起始 Cluster、Morton Key 和 StableId 排序，使用默认 64 人容量和 128 人硬上限生成 Cohort
- 成员死亡、归属移除、移动配置失效或新命令覆盖时会同步清理双向 Membership、收缩或销毁旧 Cohort，并重新发布请求成员版本与目标区域
- `AniGoalRegionAssignmentSystem` 从投影中心沿动态通行边收集可达 Cell，再按距离、地形成本、Clearance 和稳定索引选择目标区域，不会跨障碍占用其他连通区域
- Cohort 路径状态与 Flow 请求统一保存目标区域投影中心，原始目标坐标只用于动态目标跨 Cell 检测，阻挡中的静止目标不会反复递增版本
- `AniFreePreferredVelocitySystem` 远距离读取 Cohort Flow Direction，目标落点可直达后提高个人方向权重，避免所有 Ani 挤向中心
- `AniMovementCommitSystem` 同时承接历史 Squad 和正式 Cohort，但每名 Ani 只进入其中一条查询，Transform 写入边界仍然唯一

专项验收在 Unity `6000.2.7f2` Batch Mode 下完成：

- 10000 Ani 连续两轮都生成 180 个 Cohort，单组最大 64 人，切分 Hash 为 `979E69E4BBCF9309`
- 验收覆盖成员死亡、存活成员移动配置失效、130 人重叠新命令、旧 Cohort 收缩和 Membership 残留检查
- 复杂障碍专项使用动态 Overlay 封闭有限目标区域，容量不足时整单失败，不会把落点分配到障碍另一侧
- 动态目标专项覆盖阻挡目标投影、Flow 终点一致、静止目标不抖动和跨 Cell 后重新分配及重规划
- 32、64、128、512 Ani 开阔地全部到达自己的自然落点，不依赖 ORCA 或世界碰撞
- 512 Ani 两轮目标区域 Hash 为 `FA1A17890EEC4B2F`，最终位置 Hash 为 `AE3BEC88A465F1F9`
- 正式 Cohort 不携带 `AniFormationSlot`，正式 MovementOrder 不再创建 Squad

### 6A.3 共享流向场存储区（Field Store）与预算调度器

状态：已完成，2026-08-23 已通过 Unity Batch Mode 专项验收和 6A.2 回归

交付物：

- 共享 `NavigationFlowFieldHandle` 和全局 Field Store
- 唯一 Key 请求归并、优先级队列、每 Tick 构建预算和取消版本
- 多工作区并行 Field Job，以及独立的确定性发布阶段
- 按字节预算管理的缓存索引和局部 Overlay 失效
- 双缓冲或不可变快照形式的动态 Overlay 读取

退出条件：

- Field 构建次数随唯一 Key 数增长，不随 Ani 数或等待 Cohort 数增长
- 缓存命中不再向每个 Cohort 复制完整 Field
- 持续请求负载下 Overlay 更新不会无限等待
- 请求队列长度、等待 Tick P50/P95/P99、取消数和超时数全部进入报告
- 主线程不执行同步 Corridor、A* 或 Integration 搜索

实现与验收记录：

- `ServerNavigationSharedFlowFieldSystem` 只在 Server World 处理正式 Cohort；旧 `ServerNavigationGridFlowFieldSystem` 继续服务 Stage 1～5 与历史 Benchmark，两个查询不会重复消费同一请求
- Cohort 只保存 `NavigationFlowFieldHandle` 与队列状态，Corridor、Portal、Waypoint 和 Flow Cell 保存在共享 Record Entity；8 个相同 Key 只构建 1 次并产生 7 次共享命中
- 调度器支持优先级、每 Tick 构建上限、最多 8 个并行工作区、请求换代取消、排队超时和确定性发布；构建由 `IJobParallelFor` 执行，主线程仅做请求投影、调度和发布
- 动态 Overlay 以不可变 Native 快照交给活跃 Job；专项验收确认构建期间 Overlay 可以继续发布，Corridor 内变化只撤销受影响 Record，远处 Record 保持有效，Grid Blob 换代也会拒绝旧结果并重新投影请求
- Store 按有效负载字节数统计，预算不足时只淘汰无 Handle 引用的最久未用 Record；队列长度、等待 Tick 样本、取消、超时、唯一构建、共享命中和缓存字节数已接入 Benchmark 报告

### 6A.4 单位移动任务化（Job 化）

状态：功能实现已完成，2026-08-25 已通过 Unity Batch Mode 专项验收和 Stage 3、Stage 4、6A.2、6A.3 回归；万人完整回放未通过性能门禁

交付物：

- 期望速度、目标吸引、位移准备和成员进度改为 `IJobEntity`、`IJobChunk` 或等价并行 Job
- 需要随机访问的 Squad Lookup 改为只读稳定数据，避免每单位主线程 `EntityManager` 调用
- Cohort 到达判定使用成员结果归约，不在主线程对每个 Buffer 做全量随机查询
- `AniMovementCommitSystem` 保持唯一 Transform 写入者

退出条件：

- 10000 Ani 开阔地自由移动样本每 Tick 零托管 GC
- 主线程不再串行执行全部 Ani 的速度和位移计算
- Transform 提交次数与有效模拟 Ani 数和活动 Tick 严格对应
- 相同输入的最终位置 Hash、到达数和失败数重复运行一致

实现与验收记录：

- `AniFreePreferredVelocityJob` 并行计算流向场方向、个人目标吸引和加速度受限的期望速度，共享 Grid、Cohort 和 Field 数据只读访问
- `AniCohortMovementCommitJob` 与历史基线使用的 `AniSquadMovementCommitJob` 都由 `AniMovementCommitSystem` 调度，该系统仍是唯一的 `LocalTransform` 写入者
- `AniMovementResult` 保存目标版本和站稳状态，`AniFreeCohortProgressJob` 按有界成员 Buffer 归约导航分组，`AniMovementOrderProgressJob` 再通过请求自有的 Cohort 索引汇总终态，不再为每个请求扫描全部 Cohort
- 稀疏流向场在构建完成后按 `CellIndex` 排序，Ani 通过二分查找读取当前方向，查询成本不再随 Corridor 长度线性增长
- 统一基准的 `FreeCohortMovement` 工作负载已经接入真实 MovementOrder、Cohort 和共享 Field 链路，并支持 512、1000、2500、5000、10000 Ani 报告
- Ani 离开 Cohort 稀疏 Field 覆盖且尚未进入目标影响半径时，会在直达可行时直接朝个人落点移动，避免同时缺少 Flow 方向和目标吸引而陷入零速度死区
- 万人移动内核在空 Field 与目标影响半径外连续两轮完成 128 个 Tick，每名 Ani 的 Transform 提交次数均为 128，最终位置 Hash 均为 `302B5AE3CAFBB4A5`，采样窗口主线程托管分配为 `0 B`
- 完整 `FreeCohortMovement` 回放通过功能验收：10000/10000 Ani 到达，251/251 个 Cohort 完成，251/251 份路径成功，每名 Ani 提交 1320 次，未到达快照为空，主线程分配 P50/P95/P99 均为 `0 B`
- 完整回放的 Server Tick P50/P95/P99 分别为 `49.1975 ms`、`3016.4815 ms`、`3641.0771 ms`，请求排队等待 P95 为 57 Tick，共产生 938 次唯一 Field 构建且共享命中为 0；功能已通过但性能失败
- 该专项只证明并行移动、提交边界和进度归约满足 6A.4 的功能条件；性能失败必须由 6A.5 修复，不能推迟到 6C，也不能在此基线上叠加 ORCA

### 6A.5 目标流向场共享与性能修复

状态：待实现；本阶段是 6B.1 的强制前置门禁

问题基线：

- 10000 Ani 被拆成 251 个 Cohort，四轮移动请求最多形成 1004 份 Cohort 寻路需求，当前实际构建 938 份 Field
- `NavigationFlowFieldKey` 包含精确 `StartCellIndex`，相同目标和通行配置无法跨起点共享，完整回放的共享命中数为 0
- 938 次构建分别重复执行 Corridor、Integration 和 Direction 计算，每份构建还在 Worker 内创建并释放多组临时 Native 容器
- 720 个采样 Tick 中有 106 个超过 2 秒，NetCode 持续触发 Tick Batching；增加并发只能同时执行更多重复工作，不能解决根因

交付物：

- 将起点相关的 Corridor 或覆盖需求与目标相关的 Integration、Direction 数据拆成独立所有权
- 新增不包含精确起点 Cell 的目标流向场 Key，至少由目标 Cell、通行体型档位、成本配置和 Grid 版本确定共享身份
- 目标流向场按 Cluster 或固定 Tile 保存覆盖范围；新起点只补建缺失 Tile，不重建已有目标区域
- 为 Cohort 保存直达、等待覆盖、使用目标场和失败等明确路线模式；开阔地由 Cohort 级可达验证直接进入目标吸引，不提交 Field 构建
- 将 Field Job 中按请求创建的临时 Native 容器改为按并发槽位长期复用的工作区，稳定负载不发生逐请求原生容器创建和销毁
- 调度器同时限制构建数量和 Worker 时间预算，保留服务器模拟所需 Worker，不允许 Field 批次长期占满全部工作线程
- 报告新增 Corridor 解析数、目标 Record 数、覆盖 Tile 构建与复用数、直达 Cohort 数、单次构建 Worker 时间和每 Tick Field 关键路径时间
- 局部 Overlay 只使相交的覆盖 Tile 失效；目标 Record 的其他 Tile、无关 Corridor 和活动 Handle 保持有效

实现顺序：

1. 先补齐 6A.3 与 6A.4 的同机性能分段，分别记录请求收集、Corridor、Integration、Direction、发布和单位移动耗时
2. 建立目标 Record 与覆盖 Tile 数据模型，保留旧 Record 作为严格回归入口，不在一次提交中同时删除旧路径
3. 接入 Cohort 级直达模式和目标场按需扩张，再移除正式 Cohort 对精确起点 Field Key 的依赖
4. 复用构建工作区并加入 Worker 时间预算，最后执行高复用、低复用和局部 Overlay 专项

退出条件：

- 10000 Ani 开阔地同目标回放使用直达模式，Field 构建数为 0，10000/10000 Ani 在固定窗口内到达
- 10000 Ani 障碍绕行高复用场景的构建数随唯一目标、体型档位和新增覆盖 Tile 增长，不随 251 个 Cohort 线性增长；同一覆盖 Tile 只能构建一次
- 多起点共享专项必须出现有效共享命中，目标 Record 数不得退化为 Cohort 数；报告能够区分 Record 命中和 Tile 命中
- 局部 Overlay 变化只重建相交 Tile，重复发布相同版本不产生构建，解除障碍后结果可恢复且 Hash 稳定
- 采样期不发生逐请求原生容器创建和销毁，托管分配 P50/P95/P99 均为 `0 B`，可归属 Native 内存不超过 `512 MiB`
- 高复用和低复用两类 10000 Ani `FreeCohortMovement` 都满足 `Stage6A0-60Hz-v1`：Server Tick P95 不超过 `16.667 ms`、P99 不超过 `20.000 ms`、Navigation Worker 关键路径 P95 不超过 `8.000 ms`、请求排队 P95 不超过 4 Tick
- 相同输入连续运行的目标 Record、覆盖 Tile、最终位置和终态 Hash 一致，Stage 3、Stage 4、6A.2、6A.3、6A.4 全部回归通过
- 任一性能门禁失败时 6A.5 保持未完成，Benchmark Runner 返回非零退出码，禁止开始 6B.1

## 6. 阶段 6B：避碰、世界碰撞与恢复

前置条件：6A.5 的全部退出条件已经通过。空间哈希和 ORCA 不得用于掩盖 Field 构建尖峰，也不得在失败基线上继续叠加 Worker 负载。

### 6B.1 原生内存空间哈希（Native）

空间哈希以 XZ 平面位置建立，桶尺寸根据最大交互半径配置。构建和查询都必须是 Burst Job，并为每个 Ani 输出有上限的邻居切片。

邻居按距离平方、CohortId 和 StableId 稳定排序。超出上限时只保留最近的有效邻居，不允许回退到全局两两扫描。空间哈希将作为 Navigation 所有的运行时服务；战斗和感知未来可以消费只读快照，但不能在阶段六反向修改导航数据。

退出条件：

- 邻居构建与查询复杂度接近 `O(N × K)`，`K` 为配置的最大邻居数
- 10000 Ani 不创建 `N²` 容器，也不出现单桶无限增长导致的无界查询
- 相同位置和输入得到相同邻居顺序

### 6B.2 最优互惠避碰（ORCA）

每个 Ani 根据相对位置、相对速度、半径、时间窗口和让行优先级生成二维半平面约束，再选择最接近期望速度的可行解。

稳定侧向偏好由 CohortId、StableId 和相对方向决定，不再引用 SlotId。求解失败时返回有限、可诊断的最小违反速度，并限制最大速度与加速度；任何输入非有限都必须进入失败计数，不能传播到 Transform。

退出条件：

- 两群正面、十字交叉、汇流和对向窄路不会持续左右振荡
- 高密度无解时速度有限，无 NaN 或无穷值
- 单位之间不启用硬刚体推挤
- ORCA 每 Tick 零托管 GC，并记录平均、P95 和最大邻居数、约束数与降级数

### 6B.3 世界碰撞

Grid 和 Clearance 继续承担常规静态约束。Collider Cast 只用于高速、靠近世界碰撞边界、上一 Tick 发生穿透风险或 ORCA 输出无法由 Grid 证明安全的单位，不能默认对全部 10000 Ani 每 Tick 执行完整 Capsule Cast。

`AniWorldCollisionSystem` 输出 `AniSafeDisplacement`，命中后使用 Skin Width 和最多两次 Slide 修正。它不能直接写 Transform，也不能创建第二个 Commit System。

退出条件：

- 正面墙、斜墙、内角和狭缝穿透不超过配置 Skin Width
- CollisionWorld 更新顺序在 Host 和 Dedicated Server 一致
- 报告包含 Cast 数、跳过数、命中数、Slide 次数和 P95 耗时
- 10000 Ani 场景不存在周期性全员 Cast 尖峰

### 6B.4 受阻恢复与重规划

`AniStuckState` 区分以下情况：

- 有期望速度但连续低实际速度
- 到目标的 Flow 或距离连续无进展
- ORCA 连续降级
- 世界碰撞连续失败
- 当前 Field 因 Overlay 或目标版本过期

恢复按“局部等待或侧向偏移 → 目标落点重新分配 → Cohort 局部重规划 → Cohort 拆分”的顺序升级，并受 Tick 冷却和全局重规划预算限制。不能由所有受阻单位在同一 Tick 各自提交完整路径。

退出条件：

- 可恢复场景最终到达，不可恢复场景在最大时间内明确失败
- 受阻事件不会造成请求风暴
- Cohort 拆分后成员所有权、Field Handle 引用和目标版本保持一致

## 7. 阶段 6C：扩容与验收矩阵

### 7.1 固定规模

所有正式 Stage 6 Benchmark 至少覆盖：

| 规模 | 必测用途 |
|---:|---|
| 32、64、128 | 与现有 Stage 4～5 固定回放做功能回归 |
| 512 | Cohort 拆分、目标区域、基础并行移动和目标场共享回归 |
| 1000 | 多 Cohort 共享目标、正面交叉和空间哈希 |
| 2500 | 多目标、动态目标和局部 Overlay 失效 |
| 5000 | 窄口汇流、缓存压力和受阻恢复 |
| 10000 | 共享目标高复用与多目标低复用两类上限负载 |

10000 不能只测试“所有单位共享一个目标”这一种最有利情况。至少还要使用稳定分组生成多个目标和多条 Corridor，验证 Field Store、缓存预算和请求队列在低复用负载下不会失控。

### 7.2 场景矩阵

- 开阔地同目标自然聚集
- 开阔地多目标分流
- 两群正面交叉
- 十字路口四向交叉
- 宽区进入窄路再展开为自然群体
- 多 Cohort 汇入同一入口
- Follow 持续移动和转向的目标
- 目标消失、不可达或移出 Grid
- 动态建筑阻挡、解除和局部 Corridor 重选
- 靠墙、斜墙、内角和连续障碍
- 成员死亡、移除、新命令和 Cohort 拆分
- Host、Dedicated Server 和服务器专用 Null Device

### 7.3 必须采集的指标

- Server Tick P50、P95、P99 和最大值
- Navigation 各 System 主线程与 Worker 时间
- 主线程与 Job 托管分配
- 活动 Ani、MovementOrder、Cohort 和唯一 Field 数
- 请求队列长度、等待 Tick、完成、取消、失败和超时
- Field 构建、共享命中、失效、淘汰和占用字节
- HPA 展开节点、Integration Cell 和 Field Tile 数
- 空间哈希桶分布、邻居数和 ORCA 约束数
- ORCA 降级、Collider Cast、Slide、受阻和重规划次数
- 到达率、到达 Tick、目标区域占用和最小单位间距
- 位置 Hash、Cohort Hash、Field Key Hash 和最终状态 Hash
- 启用网络样本时的 Ghost 数、快照字节和发送频率

## 8. 正确性、性能与网络门禁

### 8.1 正确性

- 一个 MovementOrder 的每个合法成员恰好属于一个活动 Cohort
- Cohort 拆分、目标区域分配、邻居排序和 Field 发布不依赖 EntityQuery 返回顺序
- 路径不穿角，不进入 Clearance 不足或动态阻挡 Cell
- 单位不会为了进入已满目标中心而永久移动
- 交叉与汇流场景无永久死锁，失败场景有明确原因和最大时限
- 无 NaN、悬空 Entity、失效 Field Handle 或 NativeContainer 泄漏
- 只有 `AniMovementCommitSystem` 写入权威 Ani Transform

### 8.2 性能

- 运行时路径、目标分散、邻居、ORCA、碰撞和移动采样期零托管 GC
- 不存在随总 Ani 数平方增长的成本矩阵、邻居数组或 Field 副本
- Corridor 解析随唯一通道需求增长，目标场构建随唯一目标、体型档位和新增覆盖 Tile 增长，不随 Ani 或 Cohort 数线性增长
- 主线程不执行同步 A*、Corridor、Integration 或逐 Ani 世界碰撞循环
- 邻居和 ORCA 成本受最大邻居数约束
- Overlay、Field 淘汰和 Cohort 拆分没有周期性长尖峰
- 10000 Ani 的 Dedicated Server Null Device 样本满足 6A.0 冻结的目标 Tick 预算

### 8.3 网络与构建

- 正式命令可以引用完整的万人选择集，不截断成员
- 选择集分块、版本和 Hash 通过服务器权限与完整性校验
- 客户端不创建服务器 Field、空间哈希、ORCA 或世界碰撞 System
- Field、邻居、ORCA 约束和受阻内部状态不进入 Ghost
- 导航内核 Benchmark 与 Ghost 带宽 Benchmark 分开报告，不相互替代
- Legacy 和 Grid 后端继续互斥

## 9. 提交顺序与回滚

建议至少拆成以下独立提交：

```text
S6A-0 benchmark scales and budgets
S6A-1 server selection set and movement order
S6A-2 cohort partition and goal region
S6A-3 shared field store and scheduler
S6A-4 parallel unit movement
S6A-5 goal field sharing and performance repair
S6B-1 spatial hash
S6B-2 ORCA avoidance
S6B-3 world collision
S6B-4 stuck recovery and repath
S6C   512 to 10000 acceptance and reports
```

回滚规则：

1. 每个提交都必须可以独立编译，并保留上一个已通过的 Benchmark 入口
2. 当前严格阵型链路保留为 Stage 4～5 回归入口，正式 MovementOrder 只进入 Cohort，不能在同一 World 双写 Transform
3. 6A.5 保留精确起点 Record 作为短期回归对照，目标 Record、覆盖 Tile、直达模式和工作区复用分步提交
4. 6A.5 性能门禁未通过时不得提交 6B.1，不能用后续空间哈希或 ORCA 掩盖已有 Field 尖峰
5. ORCA、世界碰撞和 Commit 分开提交，任一阶段失败时可以回到上一层安全速度
6. 不修改或删除 Legacy Benchmark，不把阶段六的优化反向移植到 Legacy
7. 修改已有 Unity 脚本路径时保留 `.meta` GUID；新增验证入口进入 Navigation Validation 或 Benchmark 程序集

## 10. 非目标与后续阶段

以下事项不作为阶段六完成条件：

- 资源搬运迁移到 Grid
- 正式场景默认启用 Grid
- 删除 Legacy NavMesh 基线
- 10000 个完整 GameObject View、Animator、血条和 VFX 同时显示
- 战斗感知、攻击和资源系统的万人性能优化
- Navigation namespace migration

阶段六可以提供只读空间索引给未来的感知与战斗优化，但不得把这些业务迁移混入导航提交。阶段七继续负责资源搬运、正式 Prefab 与 Scene、Grid 默认后端、完整 Host/Client/Dedicated 回归和最终 Legacy 隔离。

## 11. 阶段六完成标准

只有同时满足以下条件，阶段六才可以标记完成：

- 6A.0～6A.5、6B、6C 的退出条件全部通过
- 现有 32、64、128 回归没有无法解释的正确性退化
- 10000 Ani 的高复用和低复用导航负载都具备完整报告
- 正式 Grid Pipeline 不再依赖严格阵型、职责槽位或 Hungarian 匹配
- MovementOrder、Cohort、目标区域、Field Handle、ORCA、世界碰撞和受阻恢复形成完整服务器链路
- 相同输入多轮运行的结构与结果 Hash 一致
- 采样期零托管 GC，Native 内存有明确上限和释放路径
- 主线程无同步路径搜索，唯一 Transform Commit 边界保持不变
- 所有失败都有可查询原因，Benchmark Runner 会对未到达、死锁、超时和非有限结果返回非零退出码

完成后才能进入阶段七的资源迁移和正式后端切换。阶段六报告必须记录 Unity、Entities、NetCode、Burst、硬件、目标 Tick、Git Commit、Grid Hash、命令 Hash、全部原始样本和复现命令。
