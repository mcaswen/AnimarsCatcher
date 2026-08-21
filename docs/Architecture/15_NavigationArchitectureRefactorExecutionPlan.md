# Navigation 架构重构规划与执行

[返回架构总览](README.md)

- 架构基线：[Navigation 模块架构重构方案](14_NavigationArchitectureRefactor.md)
- 行为基线：[Grid 移动实现阶段与验收标准](10_GridMovementStagesAndAcceptance.md)
- 性能基线：[Legacy NavMesh 与 Grid 性能基准](09_GridMovementImplementationBenchmark.md)
- 执行记录：[Navigation 重构执行报告（2026-08-20）](Reports/NavigationRefactor-Execution-20260820.md)

> 状态：R1-R6 已完成并通过结构、算法、Stage 1-5 与 32/64/128 双轮基准验收
>
> 范围：`Assets/Scripts/Navigation` 当前 57 个 C# 文件
>
> 原则：R1～R5 只改变职责边界、文件组织和 API 形态；R6 只在独立复现后修改算法与终态行为，并执行完整回归

## 1. 文档定位

`14_NavigationArchitectureRefactor.md` 定义最终架构和重构规则，本文定义如何安全落地。两份文档的关系是：

```text
14_NavigationArchitectureRefactor.md
    最终目录、职责、依赖和禁止事项
            ↓
15_NavigationArchitectureRefactorExecutionPlan.md
    阶段任务、执行顺序、验证、提交和回滚
            ↓
10_GridMovementStagesAndAcceptance.md
    行为验收和正式移动后端门禁
```

R1～R5 不重新设计 A*、HPA、Flow Field、Overlay 或 Squad 行为。执行中发现的算法问题先记录为 R6 Review 项，独立复现后才在 R6 修复，不混入此前的结构提交。

## 2. 当前基线

### 2.1 R0 重构前物理结构

R0 时 Navigation 仍按 Unity 实现形式组织：

```text
Assets/Scripts/Navigation/Grid/
├── Algorithms/
├── Authoring/
├── Baking/
├── Components/
├── Editor/
├── Jobs/
└── Systems/
```

这不是最终结构。上述迁移前目录已在 R5 清空并移除。

### 2.2 已有能力

- 静态 Grid 烘焙、Clearance、Region、Cluster、Portal 和 Blob
- 普通 A*、稳定端点投影、路径平滑和异步 ECS 写回
- HPA Corridor、局部 Integration Field、Flow Direction 和缓存
- 动态 Overlay、局部 Cluster 版本失效和动态 Clearance
- Squad 生命周期、Anchor、自适应阵型、职责槽位和唯一 Transform Commit
- Stage 1-5 Validation 和 Grid Benchmark 适配层

### 2.3 当前执行事实

- R0 的 43 文件结构快照与 66 条旧入口调用点已从重构前 `HEAD` 重建并持久化
- R1-R5 已执行，旧的三个大算法入口和按 Unity 类型划分的目录已移除
- Stage 1-5、Unity 编译、程序集边界、注释规范、GUID 唯一性和 Missing Script 检查均通过
- 最终代码的两轮 Stage3 Benchmark 确定性字段一致，且无 Job 安全异常
- Stage4 Squad Benchmark 已固定为 1440 Tick；32/64/128 双轮均 4/4 路径成功并全员到达
- namespace migration 按计划保留为独立后续工作，本轮未混入目录和职责重构

完整证据、环境信息和 R6 逐项结论见执行报告；namespace migration 继续作为独立后续工作。

## 3. 目标结构

最终结构以 14 号文档为准，执行时按以下垂直切片落地：

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

第一轮只移动职责和 API，不强制修改 namespace。R1-R5 稳定后，是否同步使用 `AnimarsCatcher.Navigation.Grid.*` 和 `AnimarsCatcher.Navigation.Squad` namespace，另立 Namespace Migration 提交。

## 4. 不可违反的执行规则

1. 结构重构和算法修复分离。R1-R5 不改变成本、启发式、Tie-break、平滑容差、Generation、缓存键或搜索策略。
2. 每个 R 阶段使用独立提交。阶段之间必须能编译、验证和回滚。
3. 一个阶段内先完成类型/API迁移，再移动物理文件，避免产生无法定位的大 diff。
4. 移动已有脚本时连同 `.meta` 一起移动；拆分类型时先检查 Scene、Prefab、SubScene 和 ScriptableObject 是否保存程序集限定类型名。
5. 不保留长期双份算法实现。过渡适配器必须在同一阶段末删除，否则会形成两个行为来源。
6. Solver 不访问 World、不管理 ECS 生命周期；Job 只负责 NativeContainer 和 Burst 调度；System 负责请求生命周期、版本检查和写回。
7. 所有 NativeContainer 必须在创建点声明 Allocator，在 Job 完成、异常和 OnDestroy 路径都有释放点。
8. 不把 Squad、Benchmark、Editor 代码反向放入 Grid Runtime。
9. 不以注释率为目的添加解释性废话。注释只说明约束原因、边界条件、数据协议和非显然性能设计。
10. Unity 验证必须使用唯一项目实例。batchmode 被中断后，先确认没有残留 `Unity.exe`、`UnityPackageManager.exe` 和 `Temp/UnityLockfile`，再启动下一次验证。

## 5. R0：Baseline 冻结

### 目标

在任何文件重构前固定可比较的行为、结构和性能结果。

### 执行任务

1. 记录 43 个 Navigation C# 文件的路径、程序集、namespace 和 `.meta` GUID。
2. 保存 `NavigationGridPathAlgorithms`、`NavigationGridFlowFieldAlgorithms`、`AniSquadMovementAlgorithms` 的公共入口和所有调用点。
3. 运行 Stage 1-5 Validation，保存日志和关键 fixture 输出。
4. 运行 Path / Flow Field / Squad Benchmark，保存工作负载、地图 Hash、回放 Hash 和完整 Server Tick 样本。
5. 运行 Unity 编译，确认没有基线 C# 错误。
6. 运行注释规范和程序集审计，保存 JSON 审计结果。
7. 记录当前旧类之间的依赖边，作为 R1-R5 的禁止依赖对照。

### 基线命令

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Tools\CheckCommentStyle.ps1

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Tools\AuditAssemblyMigration.ps1 `
  -JsonOutputPath Temp\AssemblyMigrationAudit-R0.json
```

Stage 1-5 使用各自的 `RunFromCommandLine` 入口，Benchmark 使用 09 号文档中登记的 batchmode 入口。R0 不能用单次成功替代重复确定性检查，至少保存两次相同输入的关键输出比较结果。

### R0 退出条件

- [x] 结构快照和调用点清单已保存
- [x] Stage 1-5 全部通过
- [x] 两次相同输入得到相同 Cell 路径、Corridor、Field 和槽位结果
- [x] Benchmark 基线结果可读且包含环境元数据
- [x] Unity 编译、注释检查和程序集审计通过

## 6. R1：Shared Runtime Rules

### 目标

先建立所有导航算法共同使用的方向、Query、Traversal 和 Cost 规则，消除 Flow Field、Squad 对 A* 类的错误依赖。

### 主要输出

```text
Grid/Static/NavigationGridDirections.cs
Grid/Runtime/NavigationGridQuery.cs
Grid/Runtime/NavigationGridTraversal.cs
Grid/Runtime/NavigationGridCost.cs
Grid/Runtime/NavigationPathRequest.cs
Grid/Runtime/NavigationDynamicOverlayView.cs  (可选)
```

### 执行顺序

1. 从现有 Grid 方向逻辑提取唯一的方向索引、偏移和 `NeighborMask` 映射。
2. 从 `NavigationGridPathQueryAlgorithms` 提取世界坐标、Cell 坐标、边界和端点投影。
3. 从 `NavigationGridPathAlgorithms` 提取静态/动态占用、边通行规则和 Clearance 规则。
4. 提取统一 Step Cost 和动态 Extra Cost 读取。
5. 让 A*、HPA、Flow Field、Overlay 和 Squad 迁移到新 Runtime API。
6. 删除重复方向映射和旧公共查询入口。
7. 检查 `NavigationGridFlowFieldAlgorithms`、Squad 系统不再调用 `NavigationGridPathAlgorithms`。

### 不能改变的行为

- 八方向编号和对角穿角检查
- `requiredClearance` 公式
- Terrain Cost、Clearance Penalty 和 Dynamic Extra Cost 公式
- 投影候选排序和稳定 CellIndex tie-break
- Overlay 无效 Cell 的防御性结果

### R1 验证

- Stage 1、Stage 2、Stage 3、Stage 4、Stage 5
- 普通 A* 与旧基线 Cell 路径逐项比较
- Flow Field Integration Cost 和方向逐项比较
- `rg` 检查不得出现 `FlowField -> NavigationGridPathAlgorithms`
- Unity 编译、Burst 编译和无新增 GC 分配检查

### R1 退出条件

- [x] Runtime 四类规则有唯一实现
- [x] Flow Field 和 HPA 不再依赖 PathAlgorithms
- [x] Squad 公共 Grid 查询不再依赖 PathAlgorithms
- [x] 原有行为输出和 Benchmark 无非预期变化

## 7. R2：A* Vertical Slice

### 目标

将单体路径完整收敛到 `Grid/Pathfinding`，让 A*、Open Set、平滑、Scratch、Job 和 System 成为一个可独立阅读的纵向切片。

### 主要迁移

```text
Grid/Pathfinding/NavigationPathData.cs
Grid/Pathfinding/NavigationGridPathfinder.cs
Grid/Pathfinding/NavigationAStarOpenSet.cs
Grid/Pathfinding/NavigationPathSmoothing.cs
Grid/Pathfinding/NavigationGridPathfindingJob.cs
Grid/Pathfinding/ServerNavigationGridPathfindingSystem.cs
```

### 执行顺序

1. 将 A* 搜索主体从 `NavigationGridPathAlgorithms` 抽出为 `NavigationGridPathfinder`。
2. 将 `NavigationGridOpenSet` 改名并收敛为 `NavigationAStarOpenSet`。
3. 将 Parent Chain 重建、Line Traversal 和 Cost-aware smoothing 抽出为 `NavigationPathSmoothing`。
4. 将 `gCosts`、`parents`、Heap、HeapPositions 和 Generations 组合为 `NavigationAStarScratch`。
5. 把 Path Data、Job 和 System 移入 Pathfinding 目录。
6. 更新所有调用点后删除 `partial NavigationGridPathAlgorithms`。
7. 保持旧 Job Result、Waypoint Buffer、Failure Reason 和写回状态不变。

### 目标 API

```csharp
NavigationPathJobResult FindPath(
    ref NavigationGridBlob grid,
    in NavigationPathJobRequest request,
    int generation,
    ref NativeList<int> output,
    ref NavigationAStarScratch scratch,
    in NavigationDynamicOverlayView overlay);
```

### R2 验证

- Stage 2 全部断言和原始 A* fixture
- Stage 3 普通 A* 可达性、路径质量和确定性对照
- Path Job 异步写回、取消、过期 Version 和 Buffer slice
- Burst 编译和 A* Benchmark

### R2 退出条件

- [x] `NavigationGridPathAlgorithms` 不再存在
- [x] A* 不再持有 Query、Traversal、Cost 的实现
- [x] A* 输出格式和性能基线保持一致
- [x] Pathfinding 可以被单独阅读和验证

## 8. R3：HPA / Flow Field 分离

### 目标

把 `NavigationGridFlowFieldAlgorithms` 拆成 HPA Corridor、Integration Field 和 Cache 三个职责，并让 Flow Field Job 只负责编排。

### 主要输出

```text
Grid/Hierarchical/NavigationCorridorData.cs
Grid/Hierarchical/NavigationGridCorridorSolver.cs
Grid/FlowField/NavigationFlowFieldData.cs
Grid/FlowField/NavigationIntegrationFieldSolver.cs
Grid/FlowField/NavigationFlowFieldCache.cs
Grid/FlowField/NavigationFlowFieldWorkspace.cs
Grid/FlowField/NavigationGridFlowFieldJob.cs
Grid/FlowField/ServerNavigationGridFlowFieldSystem.cs
```

### 执行顺序

1. 从 `NavigationFlowFieldData` 移出 Corridor Cluster、Portal 和 Hierarchical Waypoint。
2. 从旧大类抽出 Abstract Node Dijkstra、Portal Corridor 重建和 Hierarchical Waypoint 生成。
3. 形成 `NavigationGridCorridorSolver`，只返回 Corridor 结果。
4. 抽出 Corridor 内 Reverse Dijkstra、Integration Cost 和 Flow Direction。
5. 形成 `NavigationIntegrationFieldSolver`，只消费已确定 Corridor。
6. 将 Cache Key、Corridor Hash、Overlay Signature、Lookup、Insert 和 Append 形成 `NavigationFlowFieldCache`。
7. 把裸 Native 参数组合为 Cell Search、Abstract Search、FlowField Workspace、Output 和 Cache Workspace。
8. Flow Field Job 按 `Prepare → Corridor → Cache → Integration → Output` 编排。
9. 删除原 `NavigationGridFlowFieldAlgorithms`，不保留第二个大类实现。

### 不能改变的行为

- HPA Abstract Graph 的节点和边语义
- Portal Clearance 和 Corridor 顺序
- Reverse Dijkstra 的允许 Cluster 范围
- Flow Direction 的下降邻居和 fallback
- Overlay Cluster Signature、Cache Version 和 Field slice 生命周期

### R3 验证

- Stage 3 层级数据确定性和普通 A* 对照
- Corridor、Portal、Integration Cost、Flow Direction 逐项比较
- Cache hit/miss、Overlay 局部失效和 Batch output slice
- Flow Field Benchmark 的访问量、缓存命中和 Tick 样本
- `rg` 检查 Hierarchical/FlowField 不引用 Pathfinding 算法实现

### R3 退出条件

- [x] `NavigationGridFlowFieldAlgorithms` 不再存在
- [x] Corridor Solver 和 Integration Solver 独立
- [x] Cache 不拥有 ECS 生命周期，System 仍拥有 Native 容器
- [x] Flow Field Job 不重新承担搜索实现

## 9. R4：Squad 独立

### 目标

让 Squad 成为 Grid 平级消费者，明确阵型、Steering、生命周期和成员移动不属于静态 Grid。

### 主要迁移

```text
Navigation/Squad/AniSquadData.cs
Navigation/Squad/AniSquadFormationAlgorithms.cs
Navigation/Squad/AniSquadSteeringAlgorithms.cs
Navigation/Squad/AniSquadLifecycleSystem.cs
Navigation/Squad/AniSquadPlanningSystems.cs
Navigation/Squad/AniAdaptiveFormationSystem.cs
Navigation/Squad/AniSquadFormationSystems.cs
Navigation/Squad/AniSquadMovementSystems.cs
```

### 执行顺序

1. 将 `AniSquadMovementData` 中的 Squad、Formation、Member、Slot 和移动状态拆到 `AniSquadData`。
2. 将 `AniSquadMovementAlgorithms` 按 Formation 与 Steering 拆开。
3. Formation 算法只保留列数、槽位、角色和 Hungarian 匹配。
4. Steering 算法只保留 Flow 采样、Anchor 速度、Slot 速度和世界位置转换。
5. 移动 Squad System 和 Overlay/Grid 查询调用点到 Squad 目录。
6. Squad 只通过 Runtime、FlowField 和所需 Corridor Data 消费 Grid。
7. 检查 Grid 目录不存在对 Squad 类型的反向依赖。

### R4 验证

- Stage 4 的 32、64、128 Ani 阵型、生命周期、System 顺序和开阔地到达
- Stage 5 的自适应列数、职责槽位、Overlay 与槽位投影
- Squad Benchmark 到达率、阵型误差、最小间距和唯一 Commit 次数
- Grid 编译目标中不出现 `AniSquad` 类型引用

### R4 退出条件

- [x] Squad 与 Grid 物理目录平级
- [x] Formation 和 Steering 算法独立
- [x] Grid 不依赖 Squad
- [x] 唯一 Transform Commit 所有权不变

## 10. R5：Static 与 Tooling 整理

### 目标

完成静态世界、编辑器、验证和 Benchmark 的物理归位，最后才进行大范围文件移动。

### 执行顺序

1. 从静态 Grid 烘焙逻辑中抽出 `NavigationGridDirections` 和 `NavigationEuclideanDistanceTransform`。
2. 将 Authoring、Bake Asset、Baker、Blob Builder、Hierarchy Builder 和 Grid Data 移入 `Grid/Static`。
3. 将 Stage 1-5、Build Validator、Assembly Validation 移入 `Tooling/Validation`。
4. 将 Authoring Editor、Bake Utility、Inspector 和 Visualization 移入 `Tooling/Editor`。
5. 将 Benchmark Data、Synthetic Grid、Timing 和 Movement Benchmark 移入 `Tooling/Benchmark`。
6. 从 FlowField Data 和 Squad Data 中移出 Benchmark 专用类型。
7. 移动物理文件和 `.meta`，保持已有 GUID；删除空目录 `.meta`。
8. 清理旧路径、过期注释、全局 `Algorithms/Jobs/Systems/Components` 引用和文档链接。

### R5 验证

- Stage 1-5 全部通过
- Navigation Editor 程序集编译、Bake、Visualization 和 Build Validator
- Benchmark 场景加载、32/64/128 规模和 JSON 输出
- `.meta` 唯一性、Missing Script、Scene/Prefab/SubScene 导入
- `AuditAssemblyMigration.ps1` 和 `CheckCommentStyle.ps1`

### R5 退出条件

- [x] 目标目录结构与 14 号文档一致
- [x] Benchmark Data 不存在于 FlowField Runtime Data
- [x] Editor/Validation/Benchmark 不进入运行时切片
- [x] 旧目录和旧大类不再被引用

## 11. R6：算法与性能复审

R6 只能在 R1-R5 的结构验收全部通过后开始。它是问题评估阶段，不得把问题修复混入此前的结构提交。

### 复审清单

- [x] 动态 Overlay 使静态 Corridor 失效时是否需要重新选择 Corridor
- [x] Dynamic Extra Cost 是否影响宏观 Corridor 选择
- [x] Flow Direction 平滑向量抵消为零时的 fallback
- [x] 离散平滑是否可能牺牲 Bellman 最优 successor
- [x] Hierarchy Builder 中最短成本与最大 Clearance 路径的抽象边语义
- [x] Path smoothing 最坏复杂度
- [x] Cache hit 后 Field copy 和 start-cost 线性查询
- [x] Cache eviction 策略

R6 已完成。动态 Corridor、Extra Cost、Bellman 后继、零向量 fallback 和缓存换代均有独立 Stage3 夹具；Hierarchy、Smoothing 和线性缓存操作已明确语义、复杂度与性能边界。最终证据见执行报告。

每个问题必须先有独立复现、指标和行为预期，再决定是否创建算法修复任务。R6 的算法修改应单独提交，并重新运行 R0 中保存的全部基线。

## 12. 提交与回滚边界

建议每个阶段至少形成一个独立提交；如果一个阶段跨越多个高风险边界，再拆成以下小提交：

```text
R0: baseline / reports / documentation
R1: shared runtime rules
R2: pathfinding vertical slice
R3: hierarchical and flow field split
R4: squad split
R5: static and tooling placement
R6: algorithm or performance fixes, only after review
```

### 回滚规则

1. 优先整体回滚当前 R 阶段，不手工拼回单个脚本。
2. 文件移动必须连同 `.meta` 回滚，不能重新生成 GUID。
3. 不回滚用户在无关目录的工作区修改。
4. Unity `Library`、`Temp`、`Logs` 和生成的解决方案文件不作为源码回滚目标。
5. 场景、Prefab 或 ScriptableObject 出现 Missing Script 时，先回滚本阶段资源移动，再定位程序集限定名变化。

## 13. 验证矩阵

| 阶段 | 编译 | 行为验证 | 结构验证 | 性能验证 |
|---|---|---|---|---|
| R0 | Unity 全量编译 | Stage 1-5、确定性 | 文件/调用点快照 | Path、FlowField、Squad 基线 |
| R1 | Runtime + 全量编译 | Stage 1-5、规则逐项对照 | 无 FlowField → PathAlgorithms | 无非预期回归 |
| R2 | Pathfinding + 全量编译 | Stage 2、A* fixture、异步写回 | 旧 PathAlgorithms 删除 | A* 请求批次 |
| R3 | Hierarchical/FlowField + 全量编译 | Stage 3、Corridor/Field/Cache | 两个独立 Solver | Field 访问量和缓存命中 |
| R4 | Squad + 全量编译 | Stage 4-5、阵型和 Overlay | Grid 无 Squad 依赖 | 32/64/128 Squad |
| R5 | Runtime/Editor/Benchmark 全量编译 | Stage 1-5、场景导入 | 目录、asmdef、GUID、引用 | 正式 Benchmark |
| R6 | 按算法变更范围编译 | 全量基线回归 | 依赖门禁保持 | 变更前后 P50/P95/P99 |

每个阶段结束时必须同时记录：Unity 版本、Entities/NetCode 版本、Commit、地图 Hash、回放 Hash、Burst 状态、机器信息和日志路径。

## 14. 完成标准

只有以下条件全部满足，才可以把本文状态改为“完成”：

### 职责

- [x] `NavigationGridPathAlgorithms` 和 `NavigationGridFlowFieldAlgorithms` 已删除
- [x] Query、Traversal、Cost、A*、HPA、Flow Field、Squad 和 Tooling 各有明确归属
- [x] Squad 与 Grid 平级
- [x] Benchmark Data 不在 FlowField Runtime Data 中

### 依赖

- [x] Overlay 只依赖 Static
- [x] Runtime 依赖 Static + Overlay
- [x] Pathfinding 和 Hierarchical 依赖 Runtime
- [x] FlowField 依赖 Runtime + Hierarchical
- [x] Squad 依赖 Runtime + FlowField
- [x] Grid 不依赖 Squad 或 Benchmark
- [x] Tooling 只消费上述层，不被 Runtime 反向引用

### 行为和性能

- [x] Stage 1-5 全部通过
- [x] R1～R5 的 A*、Corridor、Field、Overlay、Squad 行为保持基线，R6 变更均有独立复现和预期断言
- [x] deterministic tie-breaking 保持
- [x] 无新增 GC allocation
- [x] NativeContainer 所有权和 Cache 生命周期保持
- [x] Benchmark 无非预期退化

### 可读性

新开发者仅通过目录即可定位：

```text
静态 Grid 怎么生成？  → Grid/Static
运行时能不能走？     → Grid/Runtime
动态障碍在哪里？     → Grid/Overlay
单体路径在哪里？     → Grid/Pathfinding
HPA Corridor 在哪里？→ Grid/Hierarchical
Flow Field 在哪里？  → Grid/FlowField
Squad 在哪里？       → Squad
验证和 Benchmark？   → Tooling
```

## 15. 当前下一步

R1-R6 已结束。下一步若继续 Navigation 工作，应从独立范围开始：

1. namespace migration 使用独立提交，不改写 R1-R6 的结构与算法证据
2. 只有长路径 Smoothing 或超大 Field cache-copy 的 P95 实测越界时，才引入窗口或索引结构
3. 新增动态 Overlay 场景时复用 Stage3 的 Corridor 重选夹具，并保留 32/64/128 固定窗口回归

R6 没有把未复现问题、到达阈值放宽或 namespace migration 混入既有结构提交。
