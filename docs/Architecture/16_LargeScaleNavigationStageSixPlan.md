# Navigation 阶段六：万人群体移动、ORCA 与世界碰撞执行计划

[返回架构总览](README.md)

- [目标架构：RTS 2.5D Grid 导航、群体移动与避碰](08_AdaptiveFormationNavigationPlan.md)
- [Legacy NavMesh 与 Grid 性能基准](09_GridMovementImplementationBenchmark.md)
- [Grid 移动实现阶段与验收标准](10_GridMovementStagesAndAcceptance.md)
- [Navigation R1～R6 执行与验收](15_NavigationArchitectureRefactorExecutionPlan.md)

> 状态：规划完成，尚未实施
>
> 目标：在 Server World 支持最多 10000 个同时参与导航的 Ani，并保留确定性输入、异步寻路、零托管分配和唯一 Transform 写入边界
>
> 已确认决策：正式 Grid 后端取消严格矩形或纵队阵型，不使用 ORCA 代替目标分布；采用 Movement Cohort 共享寻路、目标区域分散、空间哈希、ORCA 和选择性世界碰撞
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
| 命令容量 | `ClientSendAniCommandRpcSystem` 使用固定容量选择列表，达到容量后停止收集；服务器仍从 RPC 复制成员 | 无法在一条正式网络命令中完整表达万人选择集 |
| 命令到 Squad | `ServerAniCommandIngressSystem` 每个 RPC 生成一个命令 Entity，`AniSquadLifecycleSystem` 创建或复用一个 Squad | 把万人放入一个 Squad 会让阵型和成员维护失去规模上限 |
| 严格槽位分配 | `AniFormationAssignmentSystem` 创建 `memberCount * slotCount` 成本矩阵，再运行 Hungarian 匹配 | 10000 人成本矩阵仅 `float` 数据约 400 MB，求解复杂度接近 `O(N³)` |
| Flow Field 调度 | `ServerNavigationGridFlowFieldSystem` 每批最多 16 个请求，单个 `IJob` 内顺序处理 | 多 Cohort 或多目标突发时排队延迟不可控 |
| Flow Field 所有权 | 缓存命中后仍复制 Field 到每个 Squad Buffer；缓存最多 64 项并按整代清理 | 相同路线重复占用内存，多目标时容易缓存抖动 |
| 单位移动 | 期望速度、槽位目标、Commit 和部分进度判断仍通过 System 主线程查询遍历 | Burst 可以降低单次成本，但不能充分利用多核处理 10000 Ani |
| 动态 Overlay | Path 或 Flow Job 读取 Overlay 时，`NavigationDynamicOverlaySystem` 延迟写入 | 持续寻路负载可能增加动态障碍生效延迟 |
| 拥挤处理 | 当前 Stage 4 Benchmark 明确不包含 ORCA、世界碰撞或受阻恢复 | 开阔地到达不能证明窄路、交叉和高密度场景可用 |
| 验收规模 | 当前最终回归只覆盖 32、64、128 Ani | 没有 512～10000 的排队、内存、Worker 和 Server Tick 证据 |

这些限制要求先改规模模型，再实现 ORCA。如果直接在现有“一个大 Squad + 固定槽位”上完成旧版阶段六，之后仍需重做空间哈希分组、邻居语义、到达判定和 Field 所有权。

## 3. 目标运行时模型

### 3.1 总体链路

```text
客户端框选
    -> 服务器选择集版本
    -> MovementOrder
    -> 确定性拆分 MovementCohort
    -> Field 请求归并与预算调度
    -> 共享 FlowFieldHandle
    -> Ani Flow 期望速度 + 目标区域吸引
    -> Native 空间哈希
    -> ORCA 安全速度
    -> 选择性 Collider Cast / Slide
    -> 唯一 AniMovementCommitSystem
    -> 到达、受阻与重规划反馈
```

### 3.2 MovementOrder

`MovementOrder` 表示玩家的一次完整意图，至少保存：

- 所有者与稳定命令序号
- MoveTo、Follow 或 Find 语义
- 目标位置或目标 Entity
- 停止范围和目标区域参数
- 选择集版本或服务器 GroupId
- 创建 Tick、取消版本和优先级

移动 RPC 不再重复携带全部选中 GhostId。客户端先以分块或差量方式更新服务器选择集，服务端确认成员数量、Hash 和版本完整后，移动命令只引用该版本。后续可以评估由服务器根据框选体积重建选择集，但它不能成为第六阶段的前置假设。

服务器的 GhostId 到 Entity 索引改为随 Spawn、Despawn 增量维护，不能继续只为等待 RPC 而每 Tick 扫描全部 Ani 重建。

### 3.3 MovementCohort

`MovementCohort` 是寻路与重规划的最小共享单元。第一版使用可配置上限，默认 64 Ani，硬上限 128 Ani；最终数值以 512～10000 Benchmark 为准。

拆分过程必须稳定且接近 `O(N log N)`：

1. 按所有者、命令和 Agent 通行配置分组
2. 将单位当前位置转换成 Grid Cell 或 Cluster
3. 按 Cluster、Morton Key 和 StableId 排序
4. 每 64 个成员切成一个 Cohort，尾组不得重复或丢失成员
5. 起点相近且目标、体型和 Corridor Key 相同的 Cohort 共享同一个 Field Handle

Cohort 保存代表性起点、目标、成员范围、Field Handle、重规划状态和进度聚合。它不保存矩形列数、职责槽位或可见队形，也不要求成员保持相对顺序。

### 3.4 目标区域分散

ORCA 不能解决终点占位。如果所有 Ani 使用同一目标坐标，外围单位会持续挤向已满中心。因此第六阶段新增轻量目标区域分散，但不恢复传统阵型。

目标区域按以下规则生成：

1. 将命令目标投影到合法 Grid Cell
2. 按 Cell 距离、通行成本和稳定 CellIndex 从中心向外枚举候选区域
3. 根据 Agent 半径计算 Cell 容量，阻挡或 Clearance 不足的 Cell 不参与分配
4. 成员按起始角度、空间 Key 和 StableId 稳定排序，目标 Cell 使用相同方向顺序匹配
5. 分配只使用线性遍历和排序，不建立成员乘槽位的完整成本矩阵
6. 远离目标时单位只读取共享 Flow Direction；进入目标影响半径后才混合目标落点吸引
7. 单位进入自己的停止范围且速度稳定后释放目标争用，不再持续挤向中心

该结果可以形成不规则但稳定的自然群体。它只保证可达、不过度重叠和确定性，不保证矩形、圆形、职业前后排或视觉上的严格对齐。

### 3.5 共享 Flow Field 存储

当前每个 Squad 独立拥有 Corridor 和 Flow Field Buffer。第六阶段改为全局共享记录：

```text
NavigationFlowFieldKey
    GridHash
    TargetCell
    AgentProfile
    CostProfile
    CorridorSignature
    OverlayClusterSignature

NavigationFlowFieldHandle
    RecordId
    Generation

NavigationFlowFieldRecord
    Corridor slice
    Portal slice
    Field tile slice
    RefCount / LastUsedTick
```

同一个 Key 在同一时刻最多存在一个构建任务。所有等待的 Cohort 在发布阶段取得同一 Handle，不再把完整 Field 复制进各自 DynamicBuffer。

缓存使用哈希索引和明确的内存预算，淘汰完整 Record，不再固定为 64 项后整代清空。动态 Overlay 继续按 Corridor 涉及的 Cluster 版本失效，不允许无关分块变化使所有 Field 重建。

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

## 4. 目标 System Pipeline

服务器运行顺序调整为：

1. `ServerAniSelectionSetSystem`：组装并校验分块或差量选择集
2. `ServerAniMovementOrderIngressSystem`：校验目标与选择集版本，创建 MovementOrder
3. `AniMovementCohortPartitionSystem`：确定性拆分或复用 Cohort
4. `NavigationDynamicOverlaySnapshotSystem`：完成写缓冲并在安全 Tick 边界交换只读快照
5. `AniCohortTargetResolveSystem`：解析 MoveTo、Follow 和 Find 的当前目标
6. `NavigationFlowFieldRequestCollectSystem`：收集、归并、排序并按预算选择唯一 Field Key
7. `NavigationFlowFieldBuildSystem`：使用独立 Scratch 并行构建 Corridor 与 Field
8. `NavigationFlowFieldPublishSystem`：串行提交缓存记录和 Handle，丢弃过期版本
9. `AniGoalRegionAssignmentSystem`：只在命令、目标区域或成员版本变化时更新落点
10. `AniNeighborGridBuildSystem`：构建 Native 空间哈希和稳定邻居切片
11. `AniPreferredVelocitySystem`：并行计算 Flow、目标区域和制动速度
12. `AniLocalAvoidanceSystem`：并行求解有限邻居 ORCA 约束
13. `AniWorldCollisionSystem`：只为需要最终防穿透的单位生成安全位移
14. `AniMovementCommitSystem`：唯一写入 Ani `LocalTransform`
15. `AniMovementProgressSystem`：并行生成成员结果并按 Cohort 归约到达与受阻状态
16. `AniRepathRequestSystem`：根据动态目标、Overlay、持续受阻和预算提交下一轮请求

Field 构建 Job 不能直接并发修改共享缓存。并行阶段只写每个请求自己的固定切片或 `NativeStream`，发布阶段再按稳定 Key 顺序写入全局 Store，保持所有权和确定性清晰。

## 5. 阶段 6A：万人规模基础

### 6A.0 Benchmark 与预算基线

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

### 6A.1 选择集与 MovementOrder

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

### 6A.2 Cohort 与自由目标区域

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

### 6A.3 Field Store 与预算调度器

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

### 6A.4 单位移动 Job 化

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

## 6. 阶段 6B：避碰、世界碰撞与恢复

### 6B.1 Native 空间哈希

空间哈希以 XZ 平面位置建立，桶尺寸根据最大交互半径配置。构建和查询都必须是 Burst Job，并为每个 Ani 输出有上限的邻居切片。

邻居按距离平方、CohortId 和 StableId 稳定排序。超出上限时只保留最近的有效邻居，不允许回退到全局两两扫描。空间哈希将作为 Navigation 所有的运行时服务；战斗和感知未来可以消费只读快照，但不能在阶段六反向修改导航数据。

退出条件：

- 邻居构建与查询复杂度接近 `O(N × K)`，`K` 为配置的最大邻居数
- 10000 Ani 不创建 `N²` 容器，也不出现单桶无限增长导致的无界查询
- 相同位置和输入得到相同邻居顺序

### 6B.2 ORCA 局部避碰

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
| 512 | Cohort 拆分、目标区域和基础并行移动 |
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
- 搜索构建次数随唯一 Field Key 增长，不随 Ani 数线性增长
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
S6B-1 spatial hash
S6B-2 ORCA avoidance
S6B-3 world collision
S6B-4 stuck recovery and repath
S6C   512 to 10000 acceptance and reports
```

回滚规则：

1. 每个提交都必须可以独立编译，并保留上一个已通过的 Benchmark 入口
2. 6A.2 通过前不删除当前严格阵型链路；切换使用显式开发配置，不能在同一 World 双写 Transform
3. 共享 Field Store 上线前保留当前缓存实现作为回归对照，不在同一提交同时更换调度、缓存和 Overlay 所有权
4. ORCA、世界碰撞和 Commit 分开提交，任一阶段失败时可以回到上一层安全速度
5. 不修改或删除 Legacy Benchmark，不把阶段六的优化反向移植到 Legacy
6. 修改已有 Unity 脚本路径时保留 `.meta` GUID；新增验证入口进入 Navigation Validation 或 Benchmark 程序集

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

- 6A、6B、6C 的退出条件全部通过
- 现有 32、64、128 回归没有无法解释的正确性退化
- 10000 Ani 的高复用和低复用导航负载都具备完整报告
- 正式 Grid Pipeline 不再依赖严格阵型、职责槽位或 Hungarian 匹配
- MovementOrder、Cohort、目标区域、Field Handle、ORCA、世界碰撞和受阻恢复形成完整服务器链路
- 相同输入多轮运行的结构与结果 Hash 一致
- 采样期零托管 GC，Native 内存有明确上限和释放路径
- 主线程无同步路径搜索，唯一 Transform Commit 边界保持不变
- 所有失败都有可查询原因，Benchmark Runner 会对未到达、死锁、超时和非有限结果返回非零退出码

完成后才能进入阶段七的资源迁移和正式后端切换。阶段六报告必须记录 Unity、Entities、NetCode、Burst、硬件、目标 Tick、Git Commit、Grid Hash、命令 Hash、全部原始样本和复现命令。
