# Legacy NavMesh 与 Grid 性能基准

[返回架构总览](README.md)

- [目标架构：RTS 2.5D Grid 导航、自适应阵型与避碰](08_AdaptiveFormationNavigationPlan.md)
- [实现阶段与验收标准](10_GridMovementStagesAndAcceptance.md)

> 状态：性能对比方案，Harness 尚未实现
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
- 不修改 Legacy Ghost 类型的命名空间和程序集

Legacy 现阶段仍是活动实现。移动目录不会阻止 Unity 创建其中的 System，后端互斥完成前不能把目录位置误认为运行隔离。

## 2. 后端互斥

新增 `AniMovementBackendConfig`，后端只能取一个值：

- `ClearanceGrid`
- `LegacyNavMesh`

启动时创建对应 Tag：

- `GridMovementBackendEnabled`
- `LegacyNavMeshBackendEnabled`

Bootstrap 必须断言两个 Tag 不会同时存在。每个后端 System Group 使用 `RequireForUpdate` 等待自己的 Tag。

公共输入收敛为已通过服务器权限校验的 `AniMovementOrder`，公共输出收敛为权威 Transform 和表现速度。新旧入口不能同时消费同一个 RPC，也不能同时写 Transform。

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

## 5. 采集指标

至少记录：

- Main Thread 时间
- Worker Job 时间
- Server Tick 总时间
- P50、P95 和 P99
- GC Alloc
- 路径和 Flow Field 重建次数
- 路径、阵型、避碰和碰撞各阶段耗时
- 到达率和到达时间
- 最小单位间距
- 阵型平均误差
- 死锁、穿透和受阻重规划次数
- Ghost 快照带宽

每项测试完成预热后重复运行多次，比较中位数和 P95。不能只比较单次平均帧率。

## 6. 结果管理

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
