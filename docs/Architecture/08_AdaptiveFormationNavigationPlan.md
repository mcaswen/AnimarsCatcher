# RTS 2.5D Grid 导航、群体移动与避碰重构方案

[返回架构总览](README.md)

> 状态：阶段一至阶段四已完成；阶段五运行时主体、自动校验和 R6 算法复审已完成；阶段六 6A.0～6A.2 已完成，下一步为 6A.3 共享 Field Store 与预算调度器；阶段七尚未完成
>
> 目标实现不在编辑器或运行时使用 Unity NavMesh
>
> 原 NavMesh 实现保留为受控性能基线，不作为正式架构继续扩展
>
> 外部项目和论文仅用于理解算法，不复制源码，也不引入第三方导航依赖

## 1. 前提与结论

本方案建立在以下地图前提上：

- 一个 XZ 坐标只对应一个可行走高度
- 不需要桥下通行、多层楼面和垂直导航
- 支持平地、缓坡、有限高度差和静态障碍
- Ani 的主要运动发生在地表 XZ 平面
- 世界最终防穿透仍可以使用 Unity Physics

在这个前提下，使用自有 2.5D Clearance Grid 比继续围绕 Unity NavMesh 扩展更合适。它可以同时提供路径搜索、连通性、地形成本、通道宽度和群体通行约束，不需要在运行时调用主线程导航 API。

目标链路为：

```text
编辑器 Physics 烘焙
-> NavigationGridBlob
-> 动态障碍 Overlay 快照
-> MovementOrder 与 MovementCohort
-> HPA* Cluster Corridor
-> 共享 Integration / Flow Field Handle
-> 目标区域分散与单位期望速度
-> Native 空间哈希与 ORCA
-> 选择性 Unity Physics Capsule Cast / Slide
-> 服务器唯一提交 Transform
-> Ghost 插值到客户端
```

MoveTo、Follow、Find、资源搬运等高层业务语义可以保留，但低层路径、群体组织和运动数据重新设计。正式后端不复用 `NavAgent`、`NavWaypoint`、`NavSteering` 或 Unity NavMesh API，也不继续依赖当前严格矩形阵型作为最终移动模型。

## 2. 代码边界

当前代码位于：

```text
Assets/Scripts/Navigation/
├── Grid/
│   ├── Static/
│   ├── Runtime/
│   ├── Overlay/
│   ├── Pathfinding/
│   ├── Hierarchical/
│   └── FlowField/
├── Squad/
└── Tooling/
    ├── Editor/
    ├── Validation/
    └── Benchmark/
```

旧实现已经移动到 `Assets/Scripts/Benchmarks/LegacyNavMesh`，作为可执行性能基线保留。它不属于正式 Grid 架构，也不是 `Obsolete`。新旧后端的对比方法见 [Legacy NavMesh 与 Grid 性能基准](09_GridMovementImplementationBenchmark.md)，实施阶段和退出条件见 [Grid 移动实现阶段与验收标准](10_GridMovementStagesAndAcceptance.md)，重规划后的详细执行顺序见 [Navigation 阶段六万人群体移动执行计划](16_LargeScaleNavigationStageSixPlan.md)。

## 3. Grid 烘焙

### 3.1 Authoring 配置

`NavigationGridAuthoring` 当前配置：

- 世界 Bounds
- Cell Size
- 地面层和障碍层
- 最大可行走坡度
- 最大相邻高度差
- 基准 Agent 半径和高度
- Cluster 尺寸
- 地形区域成本
- 烘焙输出资源

Cell Size 需要同时满足路径精度、阵型宽度精度和内存预算。第一版可以从接近最小 Ani 半径的尺度开始测试，不能直接追求极小 Cell。

当前实现只保留能够容纳完整 Cell 的有效 Bounds，X 和 Z 尺寸按 `floor(configuredSize / cellSize)` 向下对齐。Bake Asset 与 Blob 保存的最大边界来自实际 Cell 范围，不把不足一个 Cell 的尾部空间声明为可查询区域。

### 3.2 地面采样

编辑器工具使用 Physics 查询完成烘焙，不使用 NavMesh：

1. 在每个 XZ Cell 中心从上向下 Raycast 地面层
2. 记录命中高度、法线和地形类型
3. 法线坡度超过阈值时标记为不可行走
4. 使用 Capsule 或 Box 占用检测排除墙体、岩石和静态阻挡
5. 围绕中心按基准 Agent 半径采样脚底支撑，排除悬崖边和过窄平台
6. 比较相邻 Cell 高度差，决定能否建立边
7. 记录 8 邻接位掩码和对应移动成本

对角移动只有在两侧正交 Cell 都可通过时才能开放，避免从障碍角点斜穿过去。

### 3.3 Clearance

在初始可行走图上计算每个 Cell 到最近不可行走 Cell 的距离，结果保存为世界单位 Clearance。

Clearance 同时用于：

- 判断不同半径 Ani 是否能进入 Cell
- 给狭窄区域增加路径成本
- 估算阵型可用宽度
- 验证槽位是否可放置
- 让锚点倾向通道中部

当前基础可行走图已经用基准 Agent Capsule 排除静态占用，因此查询更大 Agent 时只增加半径差，避免重复膨胀：

```text
requiredClearance = max(0, agentRadius - baseAgentRadius) + margin
cell.Clearance >= requiredClearance
```

Clearance 使用到阻挡 Cell 方形边界的保守距离，不允许因为对角中心距离而高估可用空间。后续如果改为不带半径的原始几何距离场，必须同时升级数据版本和所有查询公式。

两侧 Cell 都可站立但因高度差无法建立正交连接时，该断层同样作为 Clearance 距离场边界，避免悬崖两侧被误判为宽阔连续空间。

### 3.4 Region、Cluster 与 Portal

完整目标烘焙阶段继续生成：

- `RegionId`：静态连通区域编号
- `ClusterId`：HPA* 分块编号
- `Portal`：相邻 Cluster 边界上的可通过区间
- Portal 最小 Clearance
- Portal 间静态路径成本

RegionId 用于快速拒绝静态不连通目标。Cluster 和 Portal 用于大范围规划，避免每次都在完整 Cell 集合上执行 A*。

阶段一生成稳定 `RegionId` 和规则分块 `ClusterId`。阶段三已将 Portal、Portal 最小 Clearance、Portal 双端节点、Cluster 内静态成本和抽象边加入 Bake Asset 与运行时 Blob，不再使用占位结果。

### 3.5 烘焙资产与有效性

烘焙结果先保存为可检查的 `NavigationGridBakeAsset`，再由 Baker 转换为只读 `NavigationGridBlob`。

资产需要保存：

- 场景几何 Hash
- 烘焙参数 Hash
- Grid 尺寸和原点
- 烘焙工具版本
- 数据版本

构建前检查 Hash。场景几何或关键参数变化后，如果没有重新烘焙，构建检查应失败，而不是让过期导航数据进入正式包。

`NavigationGridBaker` 在 Editor Baking 和 Live Baking 中复用相同的新鲜度校验，不只依赖构建前门禁。复制场景但保留旧 SO 引用时，烘焙工具会创建新资产，不覆盖来源场景的 Grid 数据。

相同场景和参数重复烘焙必须生成相同数据 Hash，便于 Review 和性能复现。

## 4. 运行时 Grid 数据

### 4.1 NavigationGridBlob

建议的只读数据包括：

- Grid 原点、Cell Size、宽度和高度
- `NavigationCell` 数组
- Cluster 数组
- Portal 数组
- Cluster 到 Portal 的索引
- Region 信息

单个 Cell 至少保存：

- 高度
- 地面法线或压缩坡度
- 可行走标记
- 8 邻接位掩码
- Terrain Cost
- Clearance
- RegionId
- ClusterId

运行时 Blob 完全不可变，所有 System 只读共享，避免每个 World 重复复制静态地图。

### 4.2 动态障碍 Overlay

建筑、门和大型静止资源不修改 Blob，而是写入独立 Overlay：

- Cell 阻挡引用计数
- 额外移动成本
- 动态 Clearance 修正
- Cluster 版本号

使用引用计数而不是布尔值，避免多个障碍重叠时移除一个对象就错误恢复 Cell。

障碍变化只更新受影响 Cell、外围一圈 Cell 和相关 Cluster 版本。路径或 Flow Field 仅在关联 Cluster 版本变化时失效。

移动 Ani 不写入 Overlay。单位之间的动态冲突由空间哈希和 ORCA 处理，否则 Grid 会因大量移动阻挡持续失效。

## 5. 路径规划

### 5.1 起点和终点投影

项目自行实现世界位置到 Cell 的投影：

1. 计算位置对应的 Grid 坐标
2. 检查 Cell 是否可行走且 Clearance 足够
3. 不满足时按固定顺序向外搜索邻近 Cell
4. 候选按距离、地形成本和 Clearance 排序
5. 使用 RegionId 提前拒绝静态不连通的终点

搜索顺序必须稳定。相同输入不能因为 NativeContainer 遍历顺序不同而投影到不同 Cell。

### 5.2 正确性基线

第一版先实现普通 Grid A*：

- 使用八方向 Octile Distance 启发函数
- 路径成本包含距离、Terrain Cost 和低 Clearance 惩罚
- 不允许穿角
- 通过稳定 Cell Index 解决相同 F Cost 的排序
- 结果进行直线可见性平滑

普通 A* 是后续 HPA* 的正确性对照。不能一开始只实现复杂分层算法，否则路径错误难以定位。

当前实现由 `NavigationGridQuery`、`NavigationGridTraversal`、`NavigationGridCost`、`NavigationGridPathfinder`、`NavigationAStarOpenSet`、`NavigationPathSmoothing`、`NavigationGridPathfindingJob` 和 `ServerNavigationGridPathfindingSystem` 分担：

- 请求先按距离、Terrain Cost、Clearance 和 Cell Index 投影到稳定合法端点
- 起终点 Region 不一致时不展开 Open Set，直接返回 `RegionMismatch`
- Open Set 使用二叉 Heap，排序键依次为 F Cost、H Cost 和 Cell Index
- 每个请求使用独立 Generation 标识访问过的节点，不逐请求清空整张 Grid Scratch 数组
- 对角边除静态邻接外，还要求两个正交侧边满足当前 Agent 的 Clearance，防止大体型单位穿角
- 路径重建后使用 Bresenham 离散直线检查；只有直线成本不超过原 A* 分段成本和允许容差时才删除中间点
- `ServerNavigationGridPathfindingSystem` 每批最多处理 32 个请求，搜索在 Burst Job 中执行，主线程只在 Handle 已完成后写回结果
- `Version`、`Searching` 和 `Cancelled` 状态用于丢弃过期结果，路径点写入 `NavigationPathWaypoint` Buffer

普通 A* 层本身不处理动态 Overlay 生命周期、HPA、Flow Field、阵型或 Ani Transform 写入。它继续作为分层搜索的正确性基准，由后续层通过共享 Runtime 和 Overlay 规则组合使用。

### 5.3 HPA* Cluster Corridor

普通 A* 验证后增加 HPA*：

1. 在 Cluster 和 Portal 抽象图上计算宏观路线
2. 得到按顺序经过的 Cluster 和 Portal Corridor
3. 只在 Corridor 覆盖的 Cell 中执行局部搜索或 Integration Field
4. 动态 Overlay 只使相关 Corridor 或局部结果失效

宏观路径以 Squad 为单位计算，不为每个成员重复搜索。

当前实现已经提供抽象图搜索、Cluster/Portal Corridor 和路径质量/可达性对照。Path/Field Benchmark 直接为每个测试请求生成 Corridor 与 Field；Squad 运行时按队伍共享请求，不为成员重复规划。

### 5.4 局部 Integration 与 Flow Field

在当前 Corridor 内从目标方向反向计算 Integration Cost，再为 Cell 生成局部 Flow Direction。

局部 Field 当前用于：

- 推进 Squad Anchor
- 绕开 Corridor 内的小范围动态阻挡
- 给严重掉队成员提供回到队伍附近的可行方向
- 给单体移动的资源搬运对象提供共享导航能力

它不是全地图 Flow Field，只覆盖当前 Corridor。只有目标、Corridor 或相关 Overlay 版本变化时才重建。阶段六保留这种稀疏范围，但把结果改为由多个 Cohort 通过 Handle 共享，避免每个 Squad 独立复制相同 Field。

当前缓存键包含 Grid Data Hash、目标 Cell、体型 Clearance、代价参数、Corridor Hash 和相关 Cluster 版本。动态 Overlay 已接入局部 Corridor 重选、运行时成本和缓存失效；无关 Cluster 变化不会使当前 Field 失效。

## 6. Movement Cohort 与自由群体移动

### 6.1 当前严格阵型基线

当前代码仍由一条有效命令创建或更新一个服务器专用 Squad Entity，并使用 `AniSquadAnchor`、`AniSquadFormationState`、`AniFormationSlot`、`AniSlotTarget` 和确定性 Hungarian 匹配维持紧凑矩形或纵队。该链路已经通过 32、64、128 Ani 回归，必须保留到阶段 6A 的替代链路通过同规模验证后再移出正式 Pipeline。

它不适合作为万人最终模型。Hungarian 需要 `memberCount * slotCount` 成本矩阵，成员数扩大时内存按平方增长、求解时间接近立方增长；严格槽位也会让大量单位在拥挤中持续追逐固定位置。

### 6.2 MovementOrder 与 MovementCohort

阶段六把玩家命令与寻路计算批次分开：

- `MovementOrder` 保存一次完整玩家意图、目标、所有者和服务器选择集版本
- `MovementCohort` 保存有上限的成员集合、代表性起点、共享 Field Handle、重规划状态和进度
- 一条 MovementOrder 可以拆成多个 Cohort
- Cohort 按起始 Cluster、空间 Key、Agent Profile 和 StableId 确定性拆分
- 第一版默认每个 Cohort 64 Ani、硬上限 128，最终值由阶段六 Benchmark 决定

Cohort 只承担共享寻路和调度，不代表画面上的矩形、圆形或纵队，也不保存职责前后排。

### 6.3 目标区域与自然停止

ORCA 只负责局部避让，不能替代终点分布。阶段六将命令目标扩展为可占用目标区域：

- 目标先投影到合法 Grid Cell
- 候选 Cell 按距离、成本和稳定 CellIndex 从中心向外枚举
- Cell 容量根据 Agent 半径与 Clearance 决定
- 成员与候选 Cell 使用空间顺序和 StableId 做确定性线性匹配
- 远距离只消费共享 Flow Direction，接近目标后才混合自己的落点吸引
- 已经稳定停在合法区域的成员不会继续争抢中心点

目标区域只保证可达、不过度重叠和结果稳定，不保证任何可见几何阵型。分配过程不得创建完整成员乘落点成本矩阵。

### 6.4 单个 Ani

阶段六目标数据包括：

- Cohort 归属与 Agent Profile
- 目标区域落点和目标版本
- Flow 与目标吸引生成的 `AniPreferredVelocity`
- ORCA 输出的 `AniAvoidedVelocity`
- 世界碰撞输出的安全位移
- 到达、实际速度和唯一提交计数
- 连续低速、无进展、碰撞失败和重规划冷却

高层 FSM 可以继续表达 Idle、MoveTo、Follow、Find 和 Attack，但不通过通用黑板传递路径、Cell、Flow Field、邻居或逐帧速度。上述数据默认只存在于 Server World，不进入 Ghost。

### 6.5 共享 Field Handle

阶段六把当前每个 Squad 独立拥有的 Corridor 和 Flow Field Buffer 改为全局 Field Store。相同 Grid、目标、Agent Profile、代价、Corridor 和相关 Overlay 版本只构建一份记录，多个 Cohort 通过带 Generation 的 Handle 读取。

请求先归并和排序，再按 Tick 预算并行构建；共享缓存只在确定性发布阶段写入。缓存按字节预算淘汰完整记录，不再固定累计 64 项后整代清空，也不在命中时把完整 Field 复制到每个 Cohort。

## 7. 速度、避碰与世界碰撞

### 7.1 期望速度

```text
preferredVelocity = flowVelocity + goalGain * (goalPosition - aniPosition)
```

离目标较远时 `goalGain` 为零或很小，单位主要服从共享 Flow；进入目标区域后逐渐增加落点吸引。期望速度始终受最大速度、最大加速度、Grid 通行和制动距离限制。

### 7.2 空间哈希

按 XZ 平面建立 Native 空间哈希。Cell Size 接近邻居半径，每个 Ani 只读取自身和相邻 Cell。

邻居按距离平方、稳定 CohortId 和成员 StableId 排序，并限制最大邻居数，避免 O(n²) 扫描和不稳定遍历。

### 7.3 ORCA 思路的局部避碰

项目自行实现二维速度障碍求解：

1. 根据相对位置、速度、半径和时间窗口建立约束
2. 双方分担避让责任
3. 高优先级或静止对象承担更少责任
4. 在满足约束的速度集合中选择最接近期望速度的结果
5. 无法完全满足时返回最小违反量并限制加速度

ORCA 只处理动态单位之间的避碰。静态世界由 Grid、Clearance 和最终 Collider Cast 处理。

为减少对称死锁，同一和不同 Cohort 都使用 CohortId、StableId 与相对方向生成稳定侧向偏好，不再依赖 SlotId。

### 7.4 Capsule Cast 与 Slide

最终防穿透使用 Unity Physics：

1. 根据安全速度计算期望位移
2. 使用 Ani 胶囊体执行 Collider Cast
3. 命中后减去 Skin Width
4. 把剩余位移投影到碰撞平面
5. 最多执行一到两次滑动迭代
6. 执行地面高度修正
7. 由唯一 Commit System 写入 Transform

Ani 之间关闭硬刚体推挤，避免 ORCA 和刚体求解同时修正速度。

Collider Cast 可以在 Burst Job 中批量执行。CollisionWorld 更新顺序必须明确，是否开启 `SynchronizeCollisionWorld` 由动态障碍测试和 Profiler 决定。

## 8. System Pipeline

服务器专用 `AniGridMovementSystemGroup` 当前分为命令入口和运行时两个子组，已实现顺序为：

1. `ServerAniCommandIngressSystem` 或 Benchmark 入口写入统一命令
2. `NavigationDynamicOverlaySystem`
3. 正式指令进入 `AniMovementCohortPartitionSystem`，历史 Benchmark 进入 `AniSquadLifecycleSystem`
4. Cohort 或 Squad 各自解析目标并提交 Flow Field 请求
5. `ServerNavigationGridFlowFieldSystem`
6. Cohort 自由速度或 Squad 阵型速度进入同一个 `AniMovementCommitSystem`
7. `AniSquadAnchorAdvanceSystem`
8. `AniAdaptiveFormationSystem`
9. `AniFormationLayoutSystem`
10. `AniFormationAssignmentSystem`
11. `AniSlotTargetSystem`
12. `AniPreferredVelocitySystem`
13. `AniMovementCommitSystem`
14. `AniMovementProgressSystem`

当前 Pipeline 仍是 Stage 4～5 的严格阵型实现。阶段六将先用 MovementOrder、MovementCohort、目标区域和共享 Field Store 替换其中的 Anchor 阵型链路，再把 `AniNeighborGridBuildSystem`、`AniLocalAvoidanceSystem` 和 `AniWorldCollisionSystem` 放在期望速度与 Commit 之间；这些目标系统当前尚不存在。

所有顺序通过子 System Group、`UpdateAfter` 和 `UpdateBefore` 固定。只有 Commit System 可以写入 Ani Transform。

路径与 Field 计算使用 Job 和 NativeContainer。运行时主线程只负责准备请求、调度 Job 和提交已完成结果，不执行同步路径搜索。

### 8.1 分阶段落地边界

- 阶段三实现 HPA*、Corridor、局部 Field 及其 Benchmark 适配层。适配层复用同一测试场景和确定性回放，只构造路径与 Field 工作负载，不生成速度或写入 Ani Transform。
- 阶段四实现 `ServerAniCommandIngressSystem`、`AniSquadLifecycleSystem`、Anchor、基础阵型、期望速度、基础 `AniMovementCommitSystem` 和基础 `AniMovementProgressSystem`，先在开阔地跑通完整 Grid MoveTo 链路。当时正式 RPC 与 Benchmark 回放汇入同一 `AniSquadCommand`；6A.2 后只保留 Benchmark 和专项回归继续使用该契约。
- 阶段五加入动态 Overlay 和自适应阵型，不改变 Transform 写入所有权；其实现继续作为当前代码和历史性能基线。
- 阶段六拆为 6A、6B、6C：先建立万人命令、Cohort、目标区域、共享 Field 与并行移动，再加入空间哈希、ORCA、世界碰撞和受阻恢复，最后执行 512～10000 扩容验收。`AniWorldCollisionSystem` 只输出安全位移，最终仍由阶段四建立的唯一 `AniMovementCommitSystem` 写入 Transform。
- 阶段七迁移资源搬运和正式 Prefab、Scene 配置，完成正式后端切换。

阶段四的基础 Commit 不承诺拥挤避碰或硬碰撞安全，只用于开阔地端到端验收。阶段六会替换严格阵型的速度输入，但不替换 Transform 所有权；安全速度和世界碰撞约束仍必须在同一个 Commit 边界之前完成。

## 9. 资源搬运迁移

当前资源搬运系统也直接调用 `NavMesh.SamplePosition` 和 `NavMesh.CalculatePath`。若目标是正式运行时零 NavMesh，这部分必须纳入重构。

Grid 导航应提供通用请求：

- `GridRouteRequest`
- `GridRouteState`
- `GridRouteCorridor`
- `GridFlowFieldReference`
- `GridMovementTarget`

MovementCohort 和单体资源都可以使用同一静态 Grid、Overlay、端点投影和路径服务。资源不需要群体 ORCA 时，可以只消费 Flow Direction 与必要的 Capsule Cast。

完成迁移后，Legacy 目录外不应再存在 `UnityEngine.AI` 或 `NavMesh` 引用。

## 10. 实施与验收

后端互斥、命令回放、Legacy 归一化和性能指标记录在 [Legacy NavMesh 与 Grid 性能基准](09_GridMovementImplementationBenchmark.md)。阶段总表、退出条件和验收场景记录在 [Grid 移动实现阶段与验收标准](10_GridMovementStagesAndAcceptance.md)，阶段六的提交顺序和万人门禁记录在 [Navigation 阶段六万人群体移动执行计划](16_LargeScaleNavigationStageSixPlan.md)。目标架构变更时更新本文；排期、基准条件或验收阈值变化时更新对应实施文档。

## 11. 参考算法

以下资料只用于理解设计和构造测试：

- [HPA*](https://cdn.aaai.org/AIIDE/2005/AIIDE05-022.pdf)：Cluster 与 Portal 分层规划
- [Flow Field Tiles](https://www.gameaipro.com/GameAIPro/GameAIPro_Chapter23_Crowd_Pathfinding_and_Steering_Using_Flow_Field_Tiles.pdf)：局部 Integration 与方向场
- [ORCA 论文](https://doi.org/10.1007/978-3-642-19457-3_1)：互惠速度障碍与半平面约束
- [RVO2-CS](https://github.com/snape/RVO2-CS)：ORCA 输入输出和测试场景
- [Reynolds Steering Behaviors](https://www.red3d.com/cwr/steer/)：到达、追逐和速度组合
- Hungarian 与 Auction Assignment：槽位最小代价分配

实现过程改变 Grid 数据、System 顺序、后端边界或验收标准时，应先更新本方案。正式切换完成后，再把实际结构回写到 ECS 数据模型、玩法链路和关键类文档。
