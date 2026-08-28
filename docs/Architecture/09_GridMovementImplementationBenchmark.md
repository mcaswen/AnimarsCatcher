# Legacy NavMesh 与 Grid 性能基准

[返回架构总览](README.md)

- [目标架构：RTS 2.5D Grid 导航、群体移动与避碰](08_AdaptiveFormationNavigationPlan.md)
- [实现阶段与验收标准](10_GridMovementStagesAndAcceptance.md)
- [Navigation R1～R6 执行报告](Reports/NavigationRefactor-Execution-20260820.md)
- [Navigation 阶段六万人群体移动执行计划](16_LargeScaleNavigationStageSixPlan.md)

> 状态：阶段零 Harness、后端互斥和固定场景已实现；R6 后 Stage3 Path/Field 确定性复测通过，Stage4 Squad 在 32、64、128 三档双轮均全员到达；阶段六 6A.0～6A.4 已接入 512～10000 Ani 规模输入、自由移动和并行单位移动，但万人完整回放因 938 次 Field 构建、0 次共享命中和 Server Tick P95 `3016.4815 ms` 未通过性能门禁，下一步为 6A.5 目标流向场共享与性能修复；避碰、碰撞、Normalized Legacy 实机基线和完整横向对照仍待后续执行
>
> Legacy 是可执行性能基线，不是正式扩展入口

## 1. Legacy 基线

旧实现已经移动到：

```text
Assets/Scripts/Benchmarks/LegacyNavMesh/
├── Formation/
├── MovementFsm/
├── Navmesh/
├── OrderIngress/
└── PhysicsMove/
```

这里保存固定矩形阵型、逐 Ani NavMesh 路径、旧 FSM Planner、旧服务端命令消费者、分离力和射线移动。

Legacy 需要遵守：

- 保持可编译和可运行
- 只接受编译、确定性和 Benchmark Harness 接入所需修改
- 不继续增加正式玩法或主动优化热路径
- 只能在 Benchmark Scene、Development Build 或专用启动参数中启用
- 正式场景完成切换后只能启用 Grid 后端
- 阶段六迁移完成后不再随意修改 Legacy Ghost 类型的命名空间和程序集

Legacy 现阶段仍是活动实现。独立 asmdef 只提供编译隔离，不会阻止 Unity 创建其中的 System，后端互斥完成前不能把程序集边界误认为运行隔离。

## 2. 后端互斥

新增 `AniMovementBackendConfig`，后端只能取一个值：

- `ClearanceGrid`
- `LegacyNavMesh`

启动时创建对应 Tag：

- `GridMovementBackendEnabled`
- `LegacyNavMeshBackendEnabled`

Bootstrap 必须断言两个 Tag 不会同时存在。每个后端 System Group 使用 `RequireForUpdate` 等待自己的 Tag。

当前启动参数为：

- `-movement-backend=legacy`：启用 Legacy NavMesh
- `-movement-backend=grid`：启用 Clearance Grid

正式切换前未指定参数时默认使用 Legacy，避免 Grid 尚未接管 Transform 时让现有玩法失去移动能力。`AniMovementBackendGuardSystem` 在每个模拟 World 的 Initialization 阶段验证配置单例和 Tag；配置缺失、重复、错配或两个 Tag 同时存在时会记录 Error，并设置 `World.QuitUpdate` 停止后续模拟。

公共输入收敛为已通过服务器权限校验的 `AniCommandRpc`，公共输出收敛为权威 Transform 和表现速度。新旧入口不能同时消费同一个 RPC，也不能同时写 Transform。

## 3. 相同输入

Benchmark 不让两个后端在同一局同时运行。命令先记录为可回放脚本，再分别启动两个后端。

两次运行必须使用相同的：

- 地图和 Grid 烘焙版本
- Ani 初始位置、类型、半径和属性
- Benchmark Prefab 配置
- 固定 Server Tick
- 随机种子
- 命令时间、目标和选择集
- 动态障碍时间线
- Physics Filter
- 客户端和 Dedicated Server 数量

128 Ani 测试由 Harness 直接回放已校验命令，不能受旧客户端 `FixedList128Bytes<int>` 容量限制。

当前回放资产位于：

```text
Assets/SO/Benchmarks/LegacyNavigation/SO_LegacyNavigation_DefaultReplay.asset
```

回放格式由固定随机种子和按 Tick 严格递增的相对目标组成。Harness 在 Server World 内直接将整组命令写入 Ani 移动黑板，不创建 RPC，也不序列化选择列表；因此 32、64 和 128 Ani 使用完全相同的命令 Hash。正式链路已在 6A.1 改为通过服务器选择集版本引用成员，回放仍保持独立，避免把网络协议开销混入 Legacy 导航基线。

## 4. 基线归一化

当前 Legacy 有几项会让结果失真：

- `ServerNavMeshPlannerSystem` 的 `NavStop` 分支会提前结束本帧后续 Agent
- `AniMovementPlannerSystem` 的 MoveTo 分支存在逐 Ani 日志
- 旧 Ghost 导航组件会增加 Prefab 内存和网络数据
- 资源搬运也会调用 NavMesh

正式采样前需要：

1. 在独立提交中修正提前 `return` 并记录基线版本
2. 关闭逐 Ani 日志，避免日志 IO 主导耗时
3. 为 Grid 和 Legacy 使用独立 Benchmark Prefab 或构建变体
4. Ani 专项测试禁用资源搬运，资源搬运完成 Grid 迁移后单独比较
5. 保留 Raw Legacy 与 Normalized Legacy 的版本标识

归一化只修复正确性和测量噪声，不能顺便优化 Legacy 算法。

当前归一化版本为 `NormalizedLegacy-v1`，只包含两项修正：

- `ServerNavMeshPlannerSystem` 的停止分支由提前结束整个 System 改为继续处理下一个 Ani
- `AniMovementPlannerSystem` 删除逐 Ani 的 MoveTo 日志和无效队长警告

Benchmark 运行期间资源搬运、资源刷新和网络连接探针主动让出更新，防止资源路径请求、后台实例化和调试日志混入 Ani 专项样本。

## 5. 采集指标

至少记录：

- Main Thread 时间
- Worker Job 时间
- Server Tick 总时间
- P50、P95 和 P99
- GC Alloc
- 路径和 Flow Field 重建次数
- 路径、Cohort 拆分、目标区域、避碰和碰撞各阶段耗时
- 到达率和到达时间
- 最小单位间距
- 当前严格阵型基线的平均误差，以及自由群体方案的目标区域占用率
- 死锁、穿透和受阻重规划次数
- Ghost 快照带宽

每项测试完成预热后重复运行多次，比较中位数和 P95。不能只比较单次平均帧率。

固定场景为：

```text
Assets/Scenes/Benchmarks/LegacyNavigation/SCN_LegacyNavigationBenchmark.unity
```

场景中的 `LegacyNavigationBenchmarkController` 作为测试场景加载器，持有共享地图、SubScene、NavMesh、回放和默认测试参数。32、64 和 128 Ani 运行入口打开同一场景，并在进入 Play Mode 前向加载器注入本次 Ani 数量，不再复制按规模命名的场景资产。每次运行预热 120 Tick，采样 720 Tick，并在采样期第 0、180、360 和 540 Tick 回放相同目标。

无人值守运行入口为：

```text
AnimarsCatcher.Editor.LegacyNavigationBenchmarkBatchRunner.Run32FromCommandLine
AnimarsCatcher.Editor.LegacyNavigationBenchmarkBatchRunner.Run64FromCommandLine
AnimarsCatcher.Editor.LegacyNavigationBenchmarkBatchRunner.Run128FromCommandLine
```

批处理必须同时传入 `-benchmark-server-only` 和唯一的 `-movement-backend=legacy|grid`，确保只创建 Server World，避免客户端输入、渲染和网络探针进入样本。同一组三个入口会按后端等待对应结果目录，不复制场景。该参数只切换项目内的 NetCode World，不会触发 Unity 原生 Dedicated Server 编辑器模式。以 32 Ani 为例：

```powershell
Unity.exe -batchmode -projectPath <项目目录> -benchmark-server-only -movement-backend=legacy `
  -executeMethod AnimarsCatcher.Editor.LegacyNavigationBenchmarkBatchRunner.Run32FromCommandLine `
  -benchmark-git-commit=<提交哈希> -logFile <日志路径>
```

阶段三 Grid 路径与 Field 工作负载使用相同命令，只把后端改为 `grid`。结果写入 `BenchmarkResults/GridNavigation`，记录请求成功/失败、缓存命中、Field 构建次数、抽象节点访问量、Integration Cell 访问量、Grid Data Hash 和 Flow Field 系统主线程采样。该阶段不生成速度、不驱动 Ani，也不与 Legacy 的到达时间或阵型指标直接比较。

阶段四群体移动使用同一场景加载器和回放资产，在启动参数中增加 `-grid-benchmark-workload=stage4`（默认即为阶段四）。该工作负载由 Grid 系统按 32、64 或 128 Ani 创建一支 Squad，将回放目标转换为一个 `AniSquadCommand`，并运行完整开阔地移动链路。结果仍写入 `BenchmarkResults/GridNavigation`，v5 报告使用 `Workload=StrictFormationBaseline`；v4 及更早的历史报告仍保留 `Workload=SquadMovement`。报告包含 Squad 数、路径请求成功/失败、缓存命中、到达率、最小单位间距、平均阵型误差、唯一 Commit 写入次数、完整 Server Tick P50/P95/P99/最大值和主线程分配样本。它与阶段三的纯路径/Field JSON 通过 `Workload` 区分，不能混作同一指标。

2026-08-06 在提交 `997332a` 上完成三档 Grid 工作负载：32、64、128 Ani 均使用 720 个样本，所有请求成功，`TransformWriteCount=0`。Flow Field 主线程采样结果为：

| Ani 数量 | P50 | P95 | P99 | 最大值 |
|---:|---:|---:|---:|---:|
| 32 | 0.0364 ms | 0.0572 ms | 0.4064 ms | 1.2181 ms |
| 64 | 0.0547 ms | 0.0875 ms | 1.1145 ms | 1.2473 ms |
| 128 | 0.0865 ms | 0.1603 ms | 1.2329 ms | 1.3509 ms |

该采样只包围 `ServerNavigationGridFlowFieldSystem` 的主线程调度与结果写回，不包含 Worker Job 执行时间。64 和 128 Ani 日志仍出现 NetCode `Server Tick Batching`，因此这些结果可以证明寻路系统工作负载已完成，但不能单独证明完整 Server Simulation 满足最终性能门禁。

2026-08-21 的 R6 最终复测使用固定 `1440` Tick 终止窗口。Stage3 两轮均完成 160 个请求、成功 160、失败 0、缓存命中 140、构建 20，除墙钟耗时与时间戳外，确定性字段一致。Stage4 最终结果如下：

| Ani 数量 | 两轮首次终态 Tick | 两轮到达 | 两轮 Transform 写入 | P95 范围 | 主线程 Alloc P95 |
|---:|---:|---:|---:|---:|---:|
| 32 | 1015 | 32/32 | 24960 | 0.9324～1.0238 ms | 0 B |
| 64 | 1041 | 64/64 | 49920 | 1.0335～1.5208 ms | 0 B |
| 128 | 1077 | 128/128 | 99840 | 1.1527～1.3575 ms | 0 B |

这组结果证明当前开阔地 Squad 链路能够在固定输入下稳定完成，不等于阶段六拥挤避碰、世界碰撞或最终 Legacy 横向性能门禁已经通过。完整结果、Hash 和 R6 修复结论见 [Navigation R1～R6 执行报告](Reports/NavigationRefactor-Execution-20260820.md)。

## 6. 第六阶段扩容工作负载

阶段六沿用现有固定场景、命令 Hash、Grid Hash 和结果导出规则，但不能把 128 Ani Harness 直接放大后当作万人证据。新增工作负载与入口必须遵循 [阶段六执行计划](16_LargeScaleNavigationStageSixPlan.md)，并至少支持 512、1000、2500、5000 和 10000 Ani。

工作负载分为四层：

1. `StrictFormationBaseline`：只保留 32、64、128 当前严格阵型结果，用于替代链路回归
2. `FreeCohortMovement`：验证 MovementOrder、Cohort 拆分、目标区域、共享 Field 和并行移动，不启用 ORCA 与世界碰撞
3. `Avoidance`：加入空间哈希与 ORCA，覆盖同向、对向、十字交叉、汇流和高密度无解
4. `Collision`：加入选择性 Collider Cast、Slide、受阻恢复和动态 Overlay

每个规模必须同时记录高复用与低复用输入：

- 高复用：大量 Cohort 共享少量目标和 Field Key，验证共享收益与目标区域容量
- 低复用：稳定生成多个目标、起始 Cluster 和 Corridor，验证请求队列、缓存预算和淘汰

万人报告至少增加：

- MovementOrder、Cohort、唯一 Field Key 和活动 Field Handle 数
- Cohort 切分 Hash、目标区域 Hash、Field Key Hash 和最终位置 Hash
- 请求队列等待 Tick P50/P95/P99、取消、过期和超时
- Field Store 活动字节、峰值字节、构建、共享命中、失效和淘汰
- 空间哈希桶分布、邻居数、ORCA 约束数和降级数
- Collider Cast、Slide、受阻、目标重新分配和 Cohort 重规划次数

导航内核使用 Dedicated Server 或 Null Device 采样。Host、客户端渲染、GameObject View 和 Ghost 快照使用独立工作负载，不能把其中一类结果冒充另一类结果。目标 Server Tick 与各导航阶段的毫秒预算在 Stage 6A.0 基线提交中冻结，之后不得根据单次结果临时放宽。

## 7. 结果管理

每份结果需要记录：

- Git Commit
- Unity 与核心包版本
- 后端名称和基线版本
- 场景与命令脚本 Hash
- Grid Bake Hash
- 运行平台和硬件
- 测试次数、预热时长和采样时长
- 原始数据路径和汇总结论

缺少这些元数据的结果不能作为架构决策依据。对比完成后，Legacy 继续保留为回归基线，但不得重新成为正式依赖。

Harness 将汇总和逐 Tick 原始样本写入 `BenchmarkResults/LegacyNavigation`。JSON 包含 P50、P95、P99、主线程分配量、路径成功与失败次数、到达率、最小单位间距、平均阵型误差、Commit、Unity/Entities 版本、硬件、地图 Hash 和命令脚本 Hash。当前墙钟时间覆盖完整 Server Simulation Group；Worker Job 分配和 Ghost 快照带宽仍需通过 Profiler 与 NetCode Statistics 补充。
