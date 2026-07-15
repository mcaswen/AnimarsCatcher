# Grid 移动实现阶段与验收标准

[返回架构总览](README.md)

- [目标架构：RTS 2.5D Grid 导航、自适应阵型与避碰](08_AdaptiveFormationNavigationPlan.md)
- [Legacy NavMesh 与 Grid 性能基准](09_GridMovementImplementationBenchmark.md)

> 状态：阶段一 Grid 烘焙基础已实现并通过自动验收，阶段零性能基线和阶段二仍未完成
>
> 每个阶段必须满足退出条件后才能进入下一阶段

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

### 交付物

- `AniMovementBackendConfig`
- Grid 与 Legacy 互斥 Tag
- 公共 `AniMovementOrder` 输入契约
- 固定随机种子和命令回放格式
- 32、64、128 Ani Benchmark 场景
- Raw Legacy 与 Normalized Legacy 基线结果

### 验证项

- 两个后端 Tag 同时存在时立即报错并停止模拟
- 相同命令脚本多次运行产生相同命令序列
- 128 Ani 命令不受旧客户端 FixedList 容量影响
- Legacy 的脚本、Prefab 和 Scene 引用保持有效
- Benchmark 关闭逐 Ani 日志并记录归一化修复

### 退出条件

- 可以在无人工点击的情况下重复执行全部基线场景
- Legacy P50、P95 和 P99 波动处于可解释范围
- 每份结果包含 Commit、版本、硬件、场景 Hash 和脚本 Hash

## 3. 阶段一：Grid 烘焙基础

### 当前实现

- 运行时代码位于 `Assets/Scripts/Anis/Movement/Grid`
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

### 交付物

- Cluster 和 Portal 烘焙数据
- Portal 最小 Clearance 和静态成本
- 抽象图 HPA* 搜索
- Cluster Corridor
- Corridor 内 Integration Cost
- 局部 Flow Direction
- Corridor 与 Field 缓存和版本管理

### 验证项

- HPA* 路径与普通 A* 的可达性一致
- 不同 Agent 半径不会选择 Clearance 不足的 Portal
- Field 只覆盖 Corridor，不生成全地图方向场
- 目标、Corridor 或关联版本不变时复用缓存
- Field 方向不会指向不可行走 Cell
- 方向平滑后仍保持下降到目标的 Integration Cost

### 退出条件

- 大范围路径访问的 Cell 数明显低于普通全图 A*
- 路径质量满足配置允许的次优范围
- 相同目标的 Squad 可以复用合适的静态数据
- 64 和 128 Ani 场景没有主线程路径尖峰

## 6. 阶段四：Squad、Anchor 与基础阵型

### 交付物

- Squad Entity 和成员 Buffer
- Squad Order、Path State 和 Anchor
- 单个 Ani 的 Membership、Movement Config 和 Slot Target
- Anchor 沿局部 Field 推进
- 基础纵队与紧凑矩形布局
- 中心对称槽位生成
- 简单稳定槽位分配

### 验证项

- 一次移动命令只生成一个 Squad 路径上下文
- 成员加入、离开、死亡和拆队不会留下悬空引用
- Anchor 不绑定具体 Ani，队长状态变化不会造成瞬移
- 32、64、128 Ani 的槽位不重复且保持中心对称
- Planner、Anchor 和 Commit 的 System 顺序固定

### 退出条件

- 路径与 Field 重建次数随 Squad 数量增长，而不是随成员数量增长
- 开阔地 MoveTo、Follow 和 Find 能完成到达
- 成员追踪槽位时没有大规模路径交叉
- 客户端只通过 Ghost 插值观察移动结果

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

## 8. 阶段六：空间哈希、ORCA 与世界碰撞

### 交付物

- Native 空间哈希
- 稳定邻居排序和最大邻居数
- 二维 ORCA 思路的速度约束求解
- 侧向偏好、优先级和无解降级
- Capsule Cast、Skin Width 和 Slide
- 唯一 `AniMovementCommitSystem`
- 到达、受阻和重新规划状态

### 验证项

- 两队正面和十字交叉不会持续左右振荡
- 同队 SlotId 和不同 SquadId 产生稳定让行方向
- 高密度无解时速度有限且不会出现 NaN
- Ani 之间不启用硬刚体推挤
- 正面墙、斜墙和内角不会穿透
- CollisionWorld 更新顺序在 Host 和 Dedicated 上一致
- 只有 Commit System 写入权威 Transform

### 退出条件

- 多次运行交叉场景没有永久死锁
- 世界穿透不超过配置 Skin Width
- ORCA 和 Collider Cast 每 Tick 零托管 GC
- 128 Ani 场景 P95 不出现不可接受的碰撞查询尖峰

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

- 32、64、128 Ani 开阔地移动
- 宽区域进入单列窄门后重新展开
- 两队正面交叉
- 两队同时进入十字路口
- Follow 持续移动和转向的玩家
- 目标移动、消失和不可达
- 动态建筑阻挡和解除
- 队伍中途增加成员、成员死亡和拆队
- Picker、Blaster 与不同半径混合
- 靠墙、角点、狭缝和连续障碍
- 单体资源搬运
- Host、Dedicated Server 加两个客户端

每个场景都需要固定初始快照、命令脚本、随机种子、最大运行时间和明确的成功/失败判定。

## 11. 最终正确性门禁

- 相同场景和参数重复烘焙得到相同 Hash
- 相同输入重复运行得到相同 Cell 路径和稳定槽位分配
- 路径不会穿角或进入 Clearance 不足区域
- 动态障碍只使相关 Cluster 或 Corridor 失效
- 队伍在窄门前完成缩列，离开后稳定展开
- 两队交叉后不会永久死锁
- 世界穿透不超过 Skin Width
- 无 NaN、无悬空 Entity、无 NativeContainer 泄漏
- 资源、战斗和死亡不会留下失效 Squad 成员

## 12. 最终性能门禁

- 运行时路径和移动每 Tick 零托管 GC
- 主线程不执行同步路径搜索
- 路径与 Field 重建次数随 Squad 数量增长
- 128 Ani 的 Grid P95 Server Tick 优于 Normalized Legacy
- 32 Ani 的 Grid P95 不出现无法解释的显著回退
- Collider Cast、ORCA 和 Field 重建没有周期性长尖峰
- 性能报告包含 P50、P95、P99 和原始采样数据

具体毫秒阈值在阶段零完成 Legacy 基线后填写，不能凭开发机平均帧率临时决定。

## 13. 最终网络与构建门禁

- Grid 路径、Flow Field、邻居和 ORCA 约束不进入 Ghost
- 客户端不创建服务器 Grid 搜索和避碰 System
- Dedicated Server 不依赖相机、GPU Compute 或客户端场景对象
- Ghost 快照带宽不高于等价 Legacy Benchmark Prefab
- 正式构建不需要 Unity NavMesh 数据
- Legacy 只能由 Development 或 Benchmark 配置显式启用

## 14. 回滚规则

每个阶段使用独立提交，且不在同一提交中删除 Legacy。阶段验收失败时回退当前 Grid 阶段，不修改已冻结的 Legacy 行为。

正式切换前可以通过后端配置回到 Normalized Legacy。正式切换完成后，回滚必须使用明确的开发或紧急配置，不能让两个后端同时启用。

Legacy 的最终删除不是本计划的默认步骤。是否删除由负责人根据长期维护成本、发布平台和后续性能回归需求单独决定。
