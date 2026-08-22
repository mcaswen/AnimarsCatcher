# Grid 移动实现阶段与验收标准

[返回架构总览](README.md)

- [目标架构：RTS 2.5D Grid 导航、群体移动与避碰](08_AdaptiveFormationNavigationPlan.md)
- [Legacy NavMesh 与 Grid 性能基准](09_GridMovementImplementationBenchmark.md)
- [Navigation R1～R6 执行报告](Reports/NavigationRefactor-Execution-20260820.md)
- [Navigation 阶段六万人群体移动执行计划](16_LargeScaleNavigationStageSixPlan.md)

> 状态：阶段零至阶段四已完成；阶段五运行时主体、自动校验和 R6 算法复审已完成；阶段六 6A.0 Benchmark 与预算基线已完成，6A.1 及后续万人运行时链路尚未实施；阶段七及 Normalized Legacy 横向性能对照仍待执行
>
> 第六阶段内部按 6A、6B、6C 依次验收；阶段五未完成的动态 Overlay 场景验收并入 6A/6C，已取消的严格阵型正式目标不再阻塞阶段六

## 1. 实施原则

1. 先建立正确性基线，再增加 HPA*、Flow Field 和 ORCA 等优化层
2. 每个阶段交付可独立观察、测试和回退的纵向切片
3. Grid 和 Legacy 后端始终互斥，禁止双写 Transform
4. 正式运行数据使用强类型 Component，不继续扩大导航 Blackboard
5. 纯算法逻辑与 Unity Scene、World 和 GameObject 解耦，优先写 EditMode 测试
6. 所有 NativeContainer 都有明确所有者、Allocator 和释放路径
7. 每个阶段记录性能基线，不等到最终切换时才开始 Profile
8. 未完成的实验代码只能进入开发或 Benchmark 场景

## 2. 阶段零：冻结 Legacy 基线

### 当前实现

- `AniMovementBackendConfig`、`GridMovementBackendEnabled` 和 `LegacyNavMeshBackendEnabled` 已进入共享 Contracts
- `CustomBootstrap` 在创建 Server、Client 和 Thin Client World 后立即写入唯一后端配置，支持 `-movement-backend=grid|legacy`
- `AniMovementBackendGuardSystem` 会拒绝配置缺失、重复、错配和双 Tag，并停止冲突 World 的后续更新
- 所有 Legacy 移动、命令、阵型、物理和搬运 System 等待 Legacy Tag；Grid 路径服务等待 Grid Tag
- `NormalizedLegacy-v1` 已修复 NavMesh Planner 提前退出，并移除 MoveTo 逐 Ani 日志
- 共享回放 SO 使用固定种子和 Tick 命令格式，Harness 直接回放已验证命令，不经过 RPC FixedList
- 单个 Legacy Benchmark 固定场景已创建，场景加载器支持 32、64 和 128 Ani 参数并复用相同地图 Hash 与回放 Hash
- Harness 自动记录 Server Tick P50/P95/P99、主线程分配、路径次数和最终空间指标，并导出含环境元数据和原始样本的 JSON
- `LegacyNavigationBenchmarkStageZeroValidation.RunFromCommandLine` 已覆盖启动参数、Tag 互斥、冲突拒绝、128 Ani 确定性生成和单场景三组规模参数

阶段零的结构和自动化链路已经完成。不同机器和构建配置下的 Raw Legacy 与 Normalized Legacy 样本属于持续采集数据，不把单次编辑器运行结果写成固定性能阈值。

### 交付物

- `AniMovementBackendConfig`
- Grid 与 Legacy 互斥 Tag
- 公共 `AniCommandRpc` 输入契约
- 固定随机种子和命令回放格式
- 支持 32、64、128 Ani 参数的单个 Benchmark 场景
- Raw Legacy 与 Normalized Legacy 基线结果

### 验证项

- 两个后端 Tag 同时存在时立即报错并停止模拟
- 相同命令脚本多次运行产生相同命令序列
- 128 Ani 命令不受旧客户端 FixedList 容量影响
- Legacy 的脚本、Prefab 和 Scene 引用保持有效
- Benchmark 关闭逐 Ani 日志并记录归一化修复

### 退出条件

- 可以在无人工点击的情况下通过同一场景重复执行全部规模参数
- Legacy P50、P95 和 P99 波动处于可解释范围
- 每份结果包含 Commit、版本、硬件、场景 Hash 和脚本 Hash

## 3. 阶段一：Grid 烘焙基础

### 当前实现

- 运行时代码位于 `Assets/Scripts/Navigation/Grid`
- `NavigationGridAuthoring`、对齐 Bounds、中心与环形地面支撑采样、八邻接、保守 Clearance、RegionId、Bake Asset 和 Blob 已实现
- Scene 覆盖层使用缓存批量 Mesh，支持 Walkability、Clearance、Region、Slope、TerrainCost、AgentOccupancy 和邻接显示
- 数据检查窗口可查看 Hash、统计、单个 Cell 和不同 Agent 半径的占用结果
- 构建校验会检查包含 Grid Authoring 的登记场景，不要求尚未切换的 Legacy 菜单和玩法场景提前配置 Grid
- 固定验收场景为 `Assets/Scenes/Benchmarks/SCN_GridBakeStage1.unity`
- 固定验收资产为 `Assets/SO/Navigation/SO_NavigationGrid_SCN_GridBakeStage1.asset`
- `NavigationGridStageOneValidation.RunFromCommandLine` 已验证重复 Hash、穿角、台阶与断层 Clearance、边界支撑、陡坡、Region、不同半径、覆盖层抽样上限和过期检测

阶段一实现不接收移动命令，不执行 A*，也不写入 Ani Transform。阶段零的 Legacy 性能数据仍需按独立任务补齐，阶段二不得把阶段一完成误解为已经满足阶段零退出条件。

### 交付物

- `NavigationGridAuthoring`
- Physics 地面与障碍采样工具
- Cell 高度、坡度、可行走标记和 8 邻接
- 静态 Clearance 与 RegionId
- `NavigationGridBakeAsset`
- `NavigationGridBlob`
- Scene Gizmo 和数据检查窗口
- 场景几何、参数和数据版本 Hash

### 验证项

- 对角移动不会穿过两个正交障碍之间的角点
- 超过最大坡度或台阶高度的连接不会生成
- 不同半径 Agent 使用 Clearance 得到正确可行走结果
- 静态孤岛具有不同 RegionId
- Scene 覆盖层能显示阻挡、邻接、Clearance、Region、坡度、地形成本和指定 Agent 可占用性
- 场景或参数变化后旧 Bake Asset 被识别为过期

### 退出条件

- 相同输入重复烘焙得到相同 Hash
- 测试地图中的地面、坡道、窄路和障碍与 Grid 可视化一致
- 构建检查能够拒绝缺失或过期的 Grid 数据
- 烘焙流程不调用 Unity NavMesh

## 4. 阶段二：端点投影与普通 Grid A*

### 当前实现

- `NavigationGridQuery`、`NavigationGridTraversal`、`NavigationGridCost`、`NavigationGridPathfinder`、`NavigationAStarOpenSet` 和 `NavigationPathSmoothing` 已分别实现坐标转换、稳定端点投影、Agent Clearance、Region 预拒绝、Octile 启发、二叉 Heap A*、离散直线检查和代价保持平滑
- A* 的固定邻接顺序和 Open Set 排序键保证相同输入得到相同路径；相同 G Cost 时使用更小 Parent Cell Index 稳定父节点
- 对角移动同时校验烘焙邻接、目标 Cell 和两个正交侧边的当前 Agent Clearance
- `NavigationPathRequest`、`NavigationPathState` 和 `NavigationPathWaypoint` 已定义 Pending、Searching、Succeeded、Failed、Cancelled 生命周期以及稳定失败原因
- `NavigationGridPathfindingJob` 在 Burst 后台任务中顺序处理一批请求，并使用 Generation 数组复用 Scratch 内存
- `ServerNavigationGridPathfindingSystem` 在 Server 或 Local World 每批最多调度 32 个请求，Job 未完成时不会在主线程调用 `Complete`
- 完成结果按 Entity、请求 `Version` 和状态复核后写回；Entity 销毁、版本变化或取消不会写入旧路径
- `NavigationGridStageTwoValidation.RunFromCommandLine` 已验证投影、Region 快速拒绝、穿角、开放区平滑、不同体型 Clearance、Terrain Cost、重复确定性、失败状态和异步 ECS Buffer 写回

阶段二当前只提供通用路径基础设施，不消费 `AniCommandRpc`，不生成速度，也不写入 Ani Transform。Legacy 后端仍是正式场景当前使用的移动实现。

### 交付物

- 世界坐标与 Grid Cell 转换工具
- 稳定的邻近 Cell 投影
- Region 快速拒绝
- 普通 Grid A*
- Octile Distance 启发函数
- 稳定 Open Set 排序
- 路径重建和直线可见性平滑
- 路径请求、结果和失败状态 Component

### 验证项

- 起点或终点落在障碍内时能投影到最近合法 Cell
- 相同代价使用 Cell Index 得到稳定结果
- 不连通 Region 在搜索前直接失败
- 路径不会穿角或进入 Clearance 不足 Cell
- 地形成本能够改变路线选择
- 无路径、请求取消和目标失效不会泄漏 NativeContainer

### 退出条件

- 预定义地图的最短路径和失败结果全部通过测试
- 相同输入跨多次运行得到相同 Cell 路径
- 路径搜索可以在 Burst Job 中完成
- 运行时主线程不执行同步搜索

## 5. 阶段三：HPA* 与局部 Flow Field

### 当前实现

- Bake Asset 与 Blob 数据版本已升级到 v3，保存 Cluster、Portal、双端 Portal Node、抽象边和 Cluster 到 Portal Node 的稳定索引
- `NavigationGridHierarchyBuilder` 在烘焙期生成连续 Portal 区间、最小 Clearance、跨 Portal 成本和 Cluster 内静态成本
- `NavigationGridCorridorSolver`、`NavigationIntegrationFieldSolver`、`NavigationFlowFieldSolver` 和 `NavigationFlowFieldCache` 分别执行 HPA Corridor、Corridor 内 Integration Cost、下降方向生成和缓存换代；失败请求会回滚本次输出切片
- `NavigationGridFlowFieldJob` 与 `ServerNavigationGridFlowFieldSystem` 异步处理请求，每批最多 16 个，主线程只在 Job 已完成后写回结果
- Field 缓存以 Grid Data Hash、目标 Cell、体型 Clearance、代价参数、Corridor Hash 和相关 Cluster 版本为边界，并通过 Cache Version 暴露复用结果
- 统一 Benchmark 场景加载器按 `-movement-backend=grid` 注册 32、64 或 128 个纯路径请求；场景没有烘焙 Grid 时，由 `ServerNavigationGridBenchmarkGridSystem` 提供覆盖固定回放坐标的 Benchmark 专用静态 Grid
- Benchmark 专用 Grid 只衡量路径、Corridor 与 Field 工作负载，不代表正式地图碰撞数据，也不创建或写入 Ani Transform
- `NavigationGridStageThreeValidation` 覆盖层级数据确定性、普通 A* 可达性对照、25% 路径质量上限、Portal Clearance、局部 Field、缓存、World 过滤、异步写回和三种请求规模
- 2026-08-06 在提交 `997332a` 的独立验证 worktree 中完成阶段三 Editor 自动验收，入口返回码为 0；Grid 32、64、128 Ani 批处理均完成 720 个主线程样本，所有请求成功且 `TransformWriteCount=0`

### 交付物

- Cluster 和 Portal 烘焙数据
- Portal 最小 Clearance 和静态成本
- 抽象图 HPA* 搜索
- Cluster Corridor
- Corridor 内 Integration Cost
- 局部 Flow Direction
- Corridor 与 Field 缓存和版本管理
- Grid 路径与 Field Benchmark 适配层：复用单个 Benchmark 场景加载器和确定性回放，只产生路径、Corridor 与 Field 工作负载，不生成速度或写入 Ani Transform

### 验证项

- HPA* 路径与普通 A* 的可达性一致
- 不同 Agent 半径不会选择 Clearance 不足的 Portal
- Field 只覆盖 Corridor，不生成全地图方向场
- 目标、Corridor 或关联版本不变时复用缓存
- Field 方向不会指向不可行走 Cell
- 方向平滑后仍保持下降到目标的 Integration Cost
- 32、64 和 128 Ani 参数可以在同一场景中生成对应规模的 Grid 路径与 Field 工作负载
- 路径与 Field Benchmark 运行期间不存在 Ani Transform 写入

### 退出条件

- 大范围路径访问的 Cell 数明显低于普通全图 A*
- 路径质量满足配置允许的次优范围
- 相同目标的请求组可以复用合适的静态数据，阶段四的 Squad 直接消费该缓存
- Flow Field 系统自身的主线程采样已量化：64 Ani 的 P50/P95/P99/最大值为 `0.0547/0.0875/1.1145/1.2473 ms`，128 Ani 为 `0.0865/0.1603/1.2329/1.3509 ms`；两档均无超过 `2 ms` 的样本
- 完整 Server Simulation 日志仍出现 `Server Tick Batching`，该现象属于全组 Tick 调度，不等同于 Flow Field 系统自身的主线程路径尖峰；需要结合 Profiler 与 NetCode Statistics 完成与 Normalized Legacy 的横向对照

## 6. 阶段四：Squad、Anchor 与基础阵型

### 交付物

- `ServerAniCommandIngressSystem`：正式玩法消费已校验 `AniCommandRpc`，Benchmark 回放适配层绕过 RPC 容量限制后写入相同 `AniSquadCommand` 契约
- `AniSquadLifecycleSystem`：根据有效命令创建、更新和拆除 Squad 上下文
- Squad Entity 和成员 Buffer
- Squad Order、Path State 和 Anchor
- 单个 Ani 的 Membership、Movement Config 和 Slot Target
- Anchor 沿局部 Field 推进
- 基础纵队与紧凑矩形布局
- 中心对称槽位生成
- 简单稳定槽位分配
- 基础 `AniPreferredVelocitySystem`：在开阔地根据 Anchor 和 Slot Target 生成受最大速度、最大加速度约束的期望速度
- 基础 `AniMovementCommitSystem`：提交开阔地位移并成为 Grid 后端唯一 Ani Transform 写入者；本阶段不包含 ORCA 或 Capsule Cast
- 基础 `AniMovementProgressSystem`：提供到达判定和命令完成状态
- Grid 群体移动 Benchmark 适配层：同一场景加载器支持 32、64 和 128 Ani，并把确定性回放送入 Grid 命令契约

### 验证项

- 一次移动命令只生成一个 Squad 路径上下文
- 成员加入、离开、死亡和拆队不会留下悬空引用
- Anchor 不绑定具体 Ani，队长状态变化不会造成瞬移
- 32、64、128 Ani 的槽位不重复且保持中心对称
- Planner、Anchor 和 Commit 的 System 顺序固定
- `-movement-backend=grid` 时只有 Grid Harness 和 Grid 移动 System 运行，Legacy Harness 与 Legacy Transform 写入保持禁用
- 同一回放 Hash 在 32、64 和 128 Ani 参数下都能驱动 Grid 开阔地移动
- 只有基础 `AniMovementCommitSystem` 写入权威 Transform

### 退出条件

- 路径与 Field 重建次数随 Squad 数量增长，而不是随成员数量增长
- 开阔地 MoveTo 能完成到达；Follow/Find 的目标解析和动态重规划已接入，但需补充专项到达验收
- 成员追踪槽位时没有大规模路径交叉
- 客户端只通过 Ghost 插值观察移动结果

### 当前实现与验收记录

- `ServerAniCommandIngressSystem` 将正式 `AniCommandRpc` 的权限校验结果写入统一 `AniSquadCommand`；Benchmark 回放由 `ServerNavigationGridMovementBenchmarkSystem` 直接写入同一契约，不绕过 Squad 生命周期。
- `AniSquadLifecycleSystem`、`AniSquadTargetResolveSystem` 和 `AniSquadPathRequestSystem` 维护一条指令对应一个 Squad 路径上下文，并处理成员加入、离队、失效和拆队。
- `AniSquadAnchorAdvanceSystem`、`AniFormationLayoutSystem`、`AniFormationAssignmentSystem`、`AniSlotTargetSystem`、`AniPreferredVelocitySystem`、`AniMovementCommitSystem` 和 `AniMovementProgressSystem` 已形成固定的服务器更新链路。基础 Commit 是 Grid 后端唯一 Ani `LocalTransform` 写入者。
- `NavigationGridStageFourValidation.RunFromCommandLine` 已覆盖 32、64、128 Ani 的槽位唯一/中心对称、World 过滤、System 顺序、成员生命周期、开阔地 MoveTo 到达和终态稳定性；2026-08-21 R6 最终回归通过
- 阶段四 Benchmark 结果写入 `BenchmarkResults/GridNavigation`，记录 Squad 数、路径请求终态、缓存命中、到达率、最小单位间距、阵型误差、Transform 提交次数和完整 Server Tick 样本。阶段四验收不包含 ORCA、动态 Overlay、世界碰撞或受阻恢复。

R6 把 Stage4 功能终止点固定为 `WarmupTicks + SampleTicks + 600 = 1440` Tick。32、64、128 Ani 双轮分别在 1015、1041、1077 Tick 首次进入终态，六轮均为 4/4 路径成功并全员到达，主线程 Alloc P95 均为 0 B。

阶段四只建立可验证的最小完整移动链路，正确性范围限定为开阔地和静态 Grid 引导。阶段六会替换 Commit 之前的严格阵型速度输入，并增加局部避碰、硬世界碰撞、受阻恢复和拥挤场景门禁，但不得创建第二个 Transform 写入 System。

## 7. 阶段五：自适应阵型与动态 Overlay

### 交付物

- 前视 Clearance 采样
- 动态列数和收缩/展开滞回
- Picker、Blaster 职责槽位
- Hungarian 思路的低频槽位匹配
- 动态障碍 Block Count 与 Cost Overlay
- Cluster 版本失效和局部 Clearance 更新

### 验证项

- 队伍在进入窄路前完成缩列
- 离开窄路后不会频繁展开和收缩
- 槽位 Cell 的 Clearance 能容纳对应成员
- Picker 和 Blaster 优先分配到正确排位
- 多个障碍重叠时移除一个不会错误恢复 Cell
- 移动 Ani 不写入动态 Overlay
- 障碍变化只失效相关 Cluster 和 Corridor

### 退出条件

- 窄门、连续窄道和开阔区域转换场景稳定通过
- 阵型切换期间没有成员长期追逐不可达槽位
- 动态障碍加入和移除后能够重新规划并到达
- Overlay 更新没有全图重建尖峰

### 当前实现状态

阶段五运行时主体已实现，包含以下能力：

- `NavigationDynamicOverlayCell` 保存阻挡引用计数、额外移动成本、动态 Clearance 修正和 Cell 版本
- `NavigationDynamicOverlaySystem` 等待路径与 Flow Field Job 释放只读 Buffer 后消费服务端差量
- 单个 Cell 变化只更新自身外围 3x3 范围涉及的 Cluster 版本
- A* 端点投影、邻边、路径平滑、HPA 局部成本和 Flow Field 集成都读取 Overlay
- Flow Field 缓存按 Corridor Cluster 版本生成局部确定性签名，无关 Cluster 变化不会使缓存失效
- `AniAdaptiveFormationSystem` 前视采样有效 Clearance，缩列立即生效，展开使用宽度稳定滞回
- Picker/Blaster 槽位偏好和确定性的 Hungarian 最小总代价匹配已接入布局版本更新
- 槽位目标按成员半径重新投影到当前 Grid 与 Overlay 的合法 Cell

阶段五自动校验入口：

- Unity 菜单：`Tools/Animars Catcher/Navigation/Run Stage Five Validation`
- Batchmode：`NavigationGridStageFiveValidation.RunFromCommandLine`

自动校验覆盖自适应列数、职责槽位、匹配最优性与确定性、Overlay 引用计数、差量钳制和非有限输入拒绝。2026-08-20 已完成 Unity 编译和该入口验收。当前严格阵型继续作为 Stage 4～5 历史基线，但第六阶段会用 Cohort 自由移动替代正式 Pipeline；因此窄门缩列和展开不再作为最终发布目标，动态障碍、局部 Corridor 失效和场景性能验收并入 6A/6C。

## 8. 阶段六：万人自由群体移动、ORCA 与世界碰撞

完整数据模型、System Pipeline、提交顺序和万人门禁见 [Navigation 阶段六万人群体移动执行计划](16_LargeScaleNavigationStageSixPlan.md)。本节只保存阶段总表。

2026-08-22 已完成 6A.0：统一 Harness 增加 512～10000 Ani 档位、工作负载隔离、ECS DynamicBuffer 规模输入、三类确定性 Hash、v5 报告和 `Stage6A0-60Hz-v1` 预算。10000 Ani 输入连续两轮 Hash 一致，旧 32 Ani 严格阵型完整回放与 Stage Zero、Stage Four 自动验收通过。该输入工作负载不运行导航内核，不能替代后续万人移动性能门禁。

### 6A：规模基础

交付物：

- 支持服务器万人选择集版本的 MovementOrder，正式移动命令不再受 RPC FixedList 容量限制
- 按空间位置、Agent Profile 和 StableId 确定性拆分 MovementCohort，默认 64 Ani、硬上限 128 Ani，最终值由 Benchmark 冻结
- 用目标区域分散和自然停止替代矩形、纵队、职责槽位与 Hungarian 匹配
- 全局共享 Flow Field Store、Handle、唯一请求归并、优先级队列、Tick 预算和并行构建
- 动态 Overlay 只读快照或双缓冲，避免持续 Job 阻塞写入
- 期望速度、目标吸引、位移准备和成员进度 Job 化
- 512、1000、2500、5000、10000 Ani Benchmark 入口与报告格式

退出条件：

- 32、64、128 当前结果可以回归，512 Ani 自由移动基础场景全部到达
- 10000 个选择成员不会截断、重复或丢失，Cohort 不超过配置硬上限
- 不存在成员乘成员或成员乘落点的 `N²` 成本矩阵
- Field 构建次数随唯一 Key 增长，缓存命中不向每个 Cohort 复制完整 Field
- 主线程不执行同步路径搜索或串行处理全部 Ani 速度与位移
- 10000 Ani 开阔地导航样本每 Tick 零托管 GC，并满足 6A.0 冻结的目标 Tick 预算

### 6B：空间哈希、ORCA、世界碰撞与恢复

交付物：

- Native 空间哈希
- 按距离、CohortId 和 StableId 的稳定邻居排序与最大邻居数
- 二维 ORCA 思路的速度约束求解
- 侧向偏好、优先级和无解降级
- 只对存在穿透风险的单位执行 Capsule Cast、Skin Width 和 Slide
- `AniWorldCollisionSystem` 输出安全位移，由阶段四已有的唯一 `AniMovementCommitSystem` 提交
- 受阻、碰撞失败、目标落点重分配、Cohort 拆分和预算重规划状态

验证项：

- 两队正面和十字交叉不会持续左右振荡
- 同一或不同 Cohort 都能由 CohortId、StableId 和相对方向产生稳定让行方向
- 高密度无解时速度有限且不会出现 NaN
- Ani 之间不启用硬刚体推挤
- 正面墙、斜墙和内角不会穿透
- CollisionWorld 更新顺序在 Host 和 Dedicated 上一致
- 只有 Commit System 写入权威 Transform
- ORCA 和世界碰撞接入后仍沿用阶段四建立的 Commit 所有权，不新增旁路 Transform 写入

退出条件：

- 多次运行交叉场景没有永久死锁
- 世界穿透不超过配置 Skin Width
- ORCA 和 Collider Cast 每 Tick 零托管 GC
- 邻居查询复杂度受最大邻居数约束，不回退到全局两两扫描
- Collider Cast 不对全部 10000 Ani 每 Tick 无条件执行
- 受阻恢复不会让所有单位在同一 Tick 提交独立路径

### 6C：512～10000 扩容验收

至少覆盖高复用和低复用两类万人负载：前者让大量 Cohort 共享少量目标，后者生成多个目标、起始 Cluster 与 Corridor，验证请求队列和缓存压力。所有规模都记录 Server Tick、Worker、队列等待、Native 内存、Field 构建与共享、邻居、ORCA、碰撞、到达和确定性 Hash。

退出条件：

- 512、1000、2500、5000 和 10000 五档全部完成规定场景
- 高复用与低复用万人负载都满足 6A.0 冻结的目标 Tick 和内存预算
- 相同输入多轮运行的 Cohort、目标区域、Field Key、最终状态和位置 Hash 一致
- Benchmark Runner 对未到达、死锁、超时、NaN、穿透越界和容器泄漏返回非零退出码
- 阶段六完整报告包含环境、版本、原始样本和复现命令

## 9. 阶段七：资源迁移与正式切换

### 交付物

- 通用 `GridRouteRequest` 和单体 Route 消费接口
- 资源搬运 Grid 路径与移动
- Grid 专用正式 Prefab 和 Authoring
- 正式场景 Grid 后端配置
- Legacy 与 Grid 最终性能报告
- 更新后的事实架构和风险文档

### 验证项

- 资源搬运不再调用 NavMesh
- Legacy 目录外扫描不到 `UnityEngine.AI` 或 NavMesh API
- 正式 Prefab 不再携带旧 Nav Ghost 组件或内置 NavMeshAgent
- 正式场景不再依赖 NavMesh Surface、Modifier 或烘焙数据
- Host、纯 Client 和 Dedicated Server 都能完成完整玩法链路
- Grid 关闭时仍可在 Benchmark 场景启动 Legacy

### 退出条件

- 正式场景固定使用 Grid 后端
- 资源搬运、MoveTo、Follow、Find 和战斗移动均通过回归
- Grid 后端满足最终正确性、性能和网络门禁
- Legacy 仅保留为受控 Benchmark 基线

## 10. 场景验收矩阵

至少覆盖：

- 32、64、128 Ani 当前基线回归
- 512、1000、2500、5000、10000 Ani 自由群体移动
- 高复用同目标与低复用多目标
- 宽区域进入窄路后的自然汇流与离开后的自由展开
- 两队正面交叉
- 两队同时进入十字路口
- 多 Cohort 同时汇入一个入口
- Follow 持续移动和转向的玩家
- 目标移动、消失和不可达
- 动态建筑阻挡和解除
- Cohort 中途增加成员、成员死亡、拆分和新命令
- Picker、Blaster 与不同 Agent Profile 混合
- 靠墙、角点、狭缝和连续障碍
- 单体资源搬运
- Host、Dedicated Server 加两个客户端

每个场景都需要固定初始快照、命令脚本、随机种子、最大运行时间和明确的成功/失败判定。

## 11. 最终正确性门禁

- 相同场景和参数重复烘焙得到相同 Hash
- 相同输入重复运行得到相同 Cohort 切分、Cell 路径、目标区域和 Field Key
- 路径不会穿角或进入 Clearance 不足区域
- 动态障碍只使相关 Cluster 或 Corridor 失效
- 目标区域容量不足时向外稳定扩展，不让所有单位永久争抢中心点
- 两队交叉后不会永久死锁
- 世界穿透不超过 Skin Width
- 无 NaN、无悬空 Entity、无 NativeContainer 泄漏
- 资源、战斗和死亡不会留下失效 Cohort 成员或 Field Handle

## 12. 最终性能门禁

- 运行时路径和移动每 Tick 零托管 GC
- 主线程不执行同步路径搜索
- Field 构建次数随唯一请求 Key 增长，不随 Ani 数增长
- 不存在随 Ani 总数平方增长的矩阵、邻居列表或 Field 副本
- 128 Ani 的 Grid P95 Server Tick 优于 Normalized Legacy
- 32 Ani 的 Grid P95 不出现无法解释的显著回退
- 10000 Ani 高复用和低复用导航负载满足 Stage 6A.0 冻结的 Tick 与内存预算
- Collider Cast、ORCA 和 Field 重建没有周期性长尖峰
- 性能报告包含 P50、P95、P99 和原始采样数据

具体毫秒阈值在阶段零完成 Legacy 基线后填写，不能凭开发机平均帧率临时决定。

## 13. 最终网络与构建门禁

- Grid 路径、Flow Field、邻居和 ORCA 约束不进入 Ghost
- 客户端不创建服务器 Grid 搜索和避碰 System
- 正式命令通过服务器选择集版本引用万人成员，不因 FixedList 容量截断
- 选择集的分块、版本、Hash、所有权和完整性全部由服务器复核
- Dedicated Server 不依赖相机、GPU Compute 或客户端场景对象
- Ghost 快照带宽不高于等价 Legacy Benchmark Prefab
- 正式构建不需要 Unity NavMesh 数据
- Legacy 只能由 Development 或 Benchmark 配置显式启用

## 14. 回滚规则

每个阶段使用独立提交，且不在同一提交中删除 Legacy。阶段验收失败时回退当前 Grid 阶段，不修改已冻结的 Legacy 行为。

正式切换前可以通过后端配置回到 Normalized Legacy。正式切换完成后，回滚必须使用明确的开发或紧急配置，不能让两个后端同时启用。

Legacy 的最终删除不是本计划的默认步骤。是否删除由负责人根据长期维护成本、发布平台和后续性能回归需求单独决定。
