# RTS 2.5D Grid 导航、自适应阵型与避碰重构方案

[返回架构总览](README.md)

> 状态：阶段一 Grid 烘焙基础、阶段二普通 A* 路径服务、阶段三 HPA* 与局部 Flow Field、阶段四 Squad 开阔地移动链路已实现；阶段五及后续能力尚未实现
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

在这个前提下，使用自有 2.5D Clearance Grid 比继续围绕 Unity NavMesh 扩展更合适。它可以同时提供路径搜索、连通性、地形成本、通道宽度和阵型容量，不需要在运行时调用主线程导航 API。

目标链路为：

```text
编辑器 Physics 烘焙
-> NavigationGridBlob
-> 动态障碍 Overlay
-> Squad 起终点投影
-> HPA* Cluster Corridor
-> Corridor 内局部 Integration / Flow Field
-> Squad Anchor 推进
-> Clearance 前视与自适应阵型
-> 槽位分配与期望速度
-> 空间哈希与 ORCA
-> Unity Physics Capsule Cast / Slide
-> 服务器提交 Transform
-> Ghost 插值到客户端
```

MoveTo、Follow、Find、资源搬运等高层业务语义可以保留，但低层路径、阵型和运动数据重新设计。正式后端不复用 `NavAgent`、`NavWaypoint`、`NavSteering` 或 Unity NavMesh API。

## 2. 代码边界

目标代码建议放在：

```text
Assets/Scripts/Navigation/
├── Common/
├── Grid/
│   ├── Algorithms/
│   ├── Authoring/
│   ├── Baking/
│   ├── Components/
│   ├── Editor/
│   ├── Jobs/
│   ├── Systems/
│   └── Utilities/
└── Presentation/

```

旧实现已经移动到 `Assets/Scripts/Benchmarks/LegacyNavMesh`，作为可执行性能基线保留。它不属于正式 Grid 架构，也不是 `Obsolete`。新旧后端的对比方法见 [Legacy NavMesh 与 Grid 性能基准](09_GridMovementImplementationBenchmark.md)，实施阶段和退出条件见 [Grid 移动实现阶段与验收标准](10_GridMovementStagesAndAcceptance.md)。

## 3. Grid 烘焙

### 3.1 Authoring 配置

新增 `NavigationGridAuthoring`，配置：

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

当前实现位于 `NavigationGridPathAlgorithms`、`NavigationGridPathfindingJob` 和 `ServerNavigationGridPathfindingSystem`：

- 请求先按距离、Terrain Cost、Clearance 和 Cell Index 投影到稳定合法端点
- 起终点 Region 不一致时不展开 Open Set，直接返回 `RegionMismatch`
- Open Set 使用二叉 Heap，排序键依次为 F Cost、H Cost 和 Cell Index
- 每个请求使用独立 Generation 标识访问过的节点，不逐请求清空整张 Grid Scratch 数组
- 对角边除静态邻接外，还要求两个正交侧边满足当前 Agent 的 Clearance，防止大体型单位穿角
- 路径重建后使用 Bresenham 离散直线检查；只有直线成本不超过原 A* 分段成本和允许容差时才删除中间点
- `ServerNavigationGridPathfindingSystem` 每批最多处理 32 个请求，搜索在 Burst Job 中执行，主线程只在 Handle 已完成后写回结果
- `Version`、`Searching` 和 `Cancelled` 状态用于丢弃过期结果，路径点写入 `NavigationPathWaypoint` Buffer

这一阶段仍不包含动态 Overlay、HPA*、Flow Field、阵型或 Ani Transform 写入。普通 A* 保留为后续分层搜索的正确性基准，不应在阶段三被删除。

### 5.3 HPA* Cluster Corridor

普通 A* 验证后增加 HPA*：

1. 在 Cluster 和 Portal 抽象图上计算宏观路线
2. 得到按顺序经过的 Cluster 和 Portal Corridor
3. 只在 Corridor 覆盖的 Cell 中执行局部搜索或 Integration Field
4. 动态 Overlay 只使相关 Corridor 或局部结果失效

宏观路径以 Squad 为单位计算，不为每个成员重复搜索。

当前阶段三实现已经提供抽象图搜索、Cluster/Portal Corridor 和路径质量/可达性对照。现阶段 Benchmark 直接为每个测试请求生成 Corridor 与 Field；阶段四接入 Squad 后改为按 Squad 共享请求，不为成员重复规划。

### 5.4 局部 Integration 与 Flow Field

在当前 Corridor 内从目标方向反向计算 Integration Cost，再为 Cell 生成局部 Flow Direction。

局部 Field 用于：

- 推进 Squad Anchor
- 绕开 Corridor 内的小范围动态阻挡
- 给严重掉队成员提供回到队伍附近的可行方向
- 给单体移动的资源搬运对象提供共享导航能力

它不是全地图 Flow Field，只覆盖当前 Corridor。只有目标、Corridor 或相关 Overlay 版本变化时才重建。

当前缓存键包含 Grid Data Hash、目标 Cell、体型 Clearance、代价参数和 Corridor Hash。动态 Overlay 与局部 Cluster 版本失效仍属于阶段五，当前阶段只处理静态 Grid 版本。

## 6. Squad 与自适应阵型

### 6.1 Squad Entity

每次有效移动命令创建或更新服务器专用 Squad Entity，建议包含：

- `AniSquad`：拥有者、稳定 SquadId 和成员版本
- `AniSquadOrder`：命令类型、目标 Cell、目标 Entity 和命令版本
- `AniSquadPathState`：Corridor、Field 版本和失败状态
- `AniSquadAnchor`：锚点位置、朝向、速度和当前 Cell
- `AniSquadFormationState`：当前列数、目标列数和布局版本
- `AniSquadMember` Buffer
- `AniFormationSlot` Buffer
- `AniClusterCorridor` Buffer

这些数据默认只存在于 Server World，不进入 Ghost。

### 6.2 单个 Ani

单个 Ani 建议保存：

- `AniSquadMembership`
- `AniMovementConfig`
- `AniSlotTarget`
- `AniPreferredVelocity`
- `AniAvoidedVelocity`
- `AniMovementResult`
- `AniStuckState`

高层 FSM 可以表达 Idle、MoveTo、Follow、Find 和 Attack，但不再通过通用黑板传递路径、Cell、Flow Field 和逐帧速度。

### 6.3 Anchor 推进

Anchor 读取局部 Flow Direction，并受最大速度和最大加速度限制。

路径成本加入低 Clearance 惩罚，Flow Direction 再叠加轻微 Clearance 梯度，使 Anchor 倾向通道中部。否则只读取单点 Clearance 不能可靠代表左右总宽度。

Anchor 根据成员状态调节速度：

- 大部分成员跟得上时保持正常速度
- 后排持续落后时减速
- 前方需要缩列时提前减速
- 接近最终目标时制动

Anchor 不绑定具体 Ani，队长死亡或掉队不会导致阵型瞬移。

### 6.4 前视 Clearance 与列数

读取 Anchor 前视范围内的最小有效 Clearance：

```text
usableWidth = 2 * minimumClearance - 2 * boundaryMargin
columnWidth = maximumAgentDiameter + horizontalGap
columnCount = floor((usableWidth + horizontalGap) / columnWidth)
```

前视距离至少覆盖：

```text
currentSpeed * expectedReformTime + formationDepth
```

缩列立即响应，展开需要宽度持续满足时间阈值。列数变化使用宽度和时间滞回，避免边界噪声造成反复变阵。

第一阶段只实现纵队和紧凑矩形。楔形、弧形和包围布局后续再增加。

### 6.5 槽位生成与分配

槽位在 Anchor 局部空间生成，再投影到 Grid：

- Cell 必须可行走
- Cell Clearance 必须容纳对应成员
- Picker 优先前排和中排
- Blaster 优先后排
- 无效槽位过多时减少列数

只在成员版本或布局版本变化时重新分配槽位。第一版使用 Hungarian 思路，代价包含距离、换槽、职责不匹配、不可达和路径交叉。

相同代价使用稳定成员编号和 SlotId 排序，不能依赖 Entity.Index。

## 7. 速度、避碰与世界碰撞

### 7.1 期望速度

```text
preferredVelocity = anchorVelocity + slotGain * (slotPosition - aniPosition)
```

期望速度受最大速度和最大加速度限制。接近槽位时按剩余距离制动，避免越过目标后反向修正。

槽位被临时阻挡时，成员可以读取局部 Field 方向绕行，但不能离开 Corridor 或进入 Clearance 不足的 Cell。

### 7.2 空间哈希

按 XZ 平面建立 Native 空间哈希。Cell Size 接近邻居半径，每个 Ani 只读取自身和相邻 Cell。

邻居按稳定 SquadId 和成员编号排序，并限制最大邻居数，避免 O(n²) 扫描和不稳定遍历。

### 7.3 ORCA 思路的局部避碰

项目自行实现二维速度障碍求解：

1. 根据相对位置、速度、半径和时间窗口建立约束
2. 双方分担避让责任
3. 高优先级或静止对象承担更少责任
4. 在满足约束的速度集合中选择最接近期望速度的结果
5. 无法完全满足时返回最小违反量并限制加速度

ORCA 只处理动态单位之间的避碰。静态世界由 Grid、Clearance 和最终 Collider Cast 处理。

为减少对称死锁，同队按 SlotId 使用稳定侧向偏好，不同 Squad 按稳定 SquadId 决定迎面让行方向。

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

新增服务器专用 `AniGridMovementSystemGroup`，建议顺序为：

1. `NavigationDynamicOverlaySystem`
2. `ServerAniOrderIngressSystem`
3. `AniSquadLifecycleSystem`
4. `AniGridEndpointProjectionSystem`
5. `AniSquadClusterPathSystem`
6. `AniSquadLocalFlowFieldSystem`
7. `AniSquadAnchorAdvanceSystem`
8. `AniFormationLayoutSystem`
9. `AniFormationAssignmentSystem`
10. `AniPreferredVelocitySystem`
11. `AniNeighborGridBuildSystem`
12. `AniLocalAvoidanceSystem`
13. `AniWorldCollisionSystem`
14. `AniMovementCommitSystem`
15. `AniMovementProgressSystem`

所有顺序通过子 System Group、`UpdateAfter` 和 `UpdateBefore` 固定。只有 Commit System 可以写入 Ani Transform。

路径与 Field 计算使用 Job 和 NativeContainer。运行时主线程只负责准备请求、调度 Job 和提交已完成结果，不执行同步路径搜索。

### 8.1 分阶段落地边界

- 阶段三实现 HPA*、Corridor、局部 Field 及其 Benchmark 适配层。适配层复用同一测试场景和确定性回放，只构造路径与 Field 工作负载，不生成速度或写入 Ani Transform。
- 阶段四实现 `ServerAniOrderIngressSystem`、`AniSquadLifecycleSystem`、Anchor、基础阵型、期望速度、基础 `AniMovementCommitSystem` 和基础 `AniMovementProgressSystem`，先在开阔地跑通完整 Grid MoveTo 链路。正式 RPC 输入与 Benchmark 回放必须汇入相同 `AniSquadOrder` 契约。
- 阶段五加入动态 Overlay 和自适应阵型，不改变 Transform 写入所有权。
- 阶段六加入空间哈希、ORCA 和世界碰撞，并扩展受阻与重新规划状态。`AniWorldCollisionSystem` 只输出安全位移，最终仍由阶段四建立的唯一 `AniMovementCommitSystem` 写入 Transform。
- 阶段七迁移资源搬运和正式 Prefab、Scene 配置，完成正式后端切换。

阶段四的基础 Commit 不承诺拥挤避碰或硬碰撞安全，只用于开阔地端到端验收。阶段六是在同一写入边界前增加安全速度与世界碰撞约束，不是替换或并行新增 Commit System。

## 9. 资源搬运迁移

当前资源搬运系统也直接调用 `NavMesh.SamplePosition` 和 `NavMesh.CalculatePath`。若目标是正式运行时零 NavMesh，这部分必须纳入重构。

Grid 导航应提供通用请求：

- `GridRouteRequest`
- `GridRouteState`
- `GridRouteCorridor`
- `GridFlowFieldReference`
- `GridMovementTarget`

Squad 和单体资源都可以使用同一静态 Grid、Overlay、端点投影和路径服务。资源不需要阵型和 ORCA 时，可以只消费 Flow Direction 与 Capsule Cast。

完成迁移后，Legacy 目录外不应再存在 `UnityEngine.AI` 或 `NavMesh` 引用。

## 10. 实施与验收

后端互斥、命令回放、Legacy 归一化和性能指标记录在 [Legacy NavMesh 与 Grid 性能基准](09_GridMovementImplementationBenchmark.md)。实施阶段、退出条件和验收场景记录在 [Grid 移动实现阶段与验收标准](10_GridMovementStagesAndAcceptance.md)。目标架构变更时更新本文；排期、基准条件或验收阈值变化时更新对应实施文档。

## 11. 参考算法

以下资料只用于理解设计和构造测试：

- [HPA*](https://cdn.aaai.org/AIIDE/2005/AIIDE05-022.pdf)：Cluster 与 Portal 分层规划
- [Flow Field Tiles](https://www.gameaipro.com/GameAIPro/GameAIPro_Chapter23_Crowd_Pathfinding_and_Steering_Using_Flow_Field_Tiles.pdf)：局部 Integration 与方向场
- [ORCA 论文](https://doi.org/10.1007/978-3-642-19457-3_1)：互惠速度障碍与半平面约束
- [RVO2-CS](https://github.com/snape/RVO2-CS)：ORCA 输入输出和测试场景
- [Reynolds Steering Behaviors](https://www.red3d.com/cwr/steer/)：到达、追逐和速度组合
- Hungarian 与 Auction Assignment：槽位最小代价分配

实现过程改变 Grid 数据、System 顺序、后端边界或验收标准时，应先更新本方案。正式切换完成后，再把实际结构回写到 ECS 数据模型、玩法链路和关键类文档。
