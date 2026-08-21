# Navigation 重构执行报告（2026-08-20）

[返回 Navigation 执行文档](../15_NavigationArchitectureRefactorExecutionPlan.md)

## 结论

R1-R6 已全部完成。R1-R5 的职责拆分、目录迁移和程序集边界保持不变；R6 完成了算法清单复审、Squad 收敛修复、Flow/HPA/Cache 修复、固定窗口诊断协议以及最终回归。最终 Stage 1-5 全部通过，Stage3 Path/Field 两轮确定性字段一致，Stage4 Squad 在 32、64、128 三档双轮均全员到达。

本轮仍未执行 namespace migration。R6 没有放宽到达判定或延长既定终止窗口；修改集中在已独立复现的 HPA 动态边、Flow Direction、Cache 换代和 Squad 终态收敛问题。

## R0 证据

用户开始本轮时工作区已处于 R0 执行中途，因此无法从当时工作树直接保存未改动快照。结构证据改为从重构前 `HEAD`（`11d63d2`）重建：

| 证据 | 内容 | SHA-256 |
|---|---:|---|
| `NavigationRefactor-R0-Structure.csv` | 43 个脚本的路径、程序集、namespace、Meta GUID | `849b37fc388633b8b01688e24e24177395b361275fb9be5f05f5b5ccfc3c0074` |
| `NavigationRefactor-R0-Callsites.txt` | 66 条旧大类入口调用点 | `159d125bcc57ccdb365c58d7de636f5816f58f0484de7cd2a944636e68964f5d` |

这两份快照只承担结构对照。Benchmark 是在当前重构代码上取得的验证结果，不冒充重构前性能基线。

## 阶段结果

| 阶段 | 结果 |
|---|---|
| R1 Shared Runtime Rules | Query、Traversal、Cost、Directions 和 Path Request 成为共享 Runtime 契约；Flow/HPA/Squad 不再依赖 Pathfinding 实现 |
| R2 A* Vertical Slice | A*、Open Set、Scratch、Smoothing、Job、System 收敛到 `Grid/Pathfinding`；旧 PathAlgorithms 删除 |
| R3 HPA / Flow Field | Corridor、Integration、Cache、Coordinator 拆分；旧 FlowFieldAlgorithms 删除 |
| R4 Squad | Squad 与 Grid 平级；Formation 与 Steering 拆分；Grid 对 Squad 的反向依赖清零 |
| R5 Static / Tooling | Static、Editor、Validation、Benchmark 完成归位；Benchmark 专用数据移出 Runtime；旧类型目录删除 |

运行时程序集仍使用 `AnimarsCatcher.Navigation`；Tooling 细分为 Editor、Validation、Benchmark 程序集。运行时不反向引用 Tooling。

## 验证结果

- Unity：`6000.2.7f2`，Windows 11，AMD Ryzen 9 9950X3D，32 逻辑处理器，128568 MB 内存，Null Device
- Stage 1-5：全部通过
- Unity batchmode 编译：0 个 C# 编译错误
- `AuditAssemblyMigration.ps1`：0 个边界违规、0 个警告、0 个关键问题
- `CheckCommentStyle.ps1`：15.01%，通过项目 15% 门槛
- `.meta` GUID：无重复；脚本均有 `.meta`；未发现 `m_Script: {fileID: 0}`
- 旧类引用：`NavigationGridPathAlgorithms`、`NavigationGridFlowFieldAlgorithms`、`AniSquadMovementAlgorithms`、`NavigationGridRuntimeRules`、`NavigationGridAlgorithms` 均为 0
- 异步 Job：Path/Flow 句柄登记到 `SystemState.Dependency`；最终四轮 Benchmark 日志的 `InvalidOperationException` 均为 0

持久日志位于 `Logs/NavigationRefactor/`。该目录和 `BenchmarkResults/` 属于本机验证产物，不作为源码提交内容。

## 最终 Benchmark

共同输入：32 Ani、Git `11d63d2`、Map Hash `b14adf5dd8494058a3c2b236b10f4ea94dec75802a55e549f182d193ff9886e4`、Replay Hash `6b938171c7c5765a0d42073d031e0488896cfa74c55a79587abda4ed0ac555f5`、Grid Bake Hash `4b6e63686d61726b4772696444617431`。

### R1-R5 Stage3 Path / Flow 历史基线

| 结果文件 | 提交/完成/成功/失败 | Cache/Build | Abstract/Integration | P50/P95/P99 ms |
|---|---|---|---|---|
| `GridNavigation_32_20260820_112609.json` | 128/160/160/0 | 140/20 | 98175/6400 | 0.0523/0.0722/0.3518 |
| `GridNavigation_32_20260820_112709.json` | 128/160/160/0 | 140/20 | 98175/6400 | 0.0507/0.0703/0.3661 |

除耗时序列和时间戳外，路径与 Field 的确定性字段一致。

### R6 输入：Stage4 Squad 失败基线

| 结果文件 | Path | 到达 | Transform 写入 | 最小间距 | 编队误差 | P50/P95/P99 ms |
|---|---|---:|---:|---:|---:|---|
| `GridNavigation_32_20260820_112829.json` | 4/4 | 0/32 | 13440 | 1.379229 | 15.824133 | 0.8694/1.8758/3.5880 |
| `GridNavigation_32_20260820_112929.json` | 4/4 | 0/32 | 14752 | 1.378407 | 15.825360 | 0.8769/1.9652/3.8468 |

两轮路径结果一致，但 Transform 写入相差 1312，间距和编队误差也有漂移。两轮到达率均为 0。该结果登记为 R6：先确认 Benchmark 是否按固定 Server Tick 终止，再判断是否存在 Squad 运动或到达判定问题。

## R6 完成结果

本轮在固定输入下把阶段四 Benchmark 的功能终止点固定为：

```text
TerminationTick = WarmupTicks + SampleTicks + 600
```

结果导出前会在下一次 Runtime Tick 复核 Squad 仍处于 `Completed` 或 `Holding`，失败结果写入 `Failed` 和 `FailureReason`，批处理 Runner 根据该字段返回非零退出码。诊断模式通过 `-grid-benchmark-trace` 开启，默认关闭，不改变正式性能样本；报告 `FormatVersion=4` 增加 StateTrace 和 AgentTrace，记录目标位置、Anchor 距离、成员最大槽位误差、速度门限、槽位索引和唯一 Transform 提交计数。

### Squad 复现与修复

诊断 Trace 分离出了四个根因：

1. Benchmark 原先按墙钟时机停止，终态样本数会漂移；现固定在 `Warmup + Sample + 600 = 1440` Tick，并由 Runner 拒绝 `Failed` 报告。
2. `ConfiguredColumnCount` 未在生命周期系统持久化，64/128 人阵型会退化成过深单列；现由 Squad 创建和命令更新共同维护最大列数。
3. 完成后自适应阵型和 Anchor 朝向仍会改变槽位，导致先完成再离开终态；终态现冻结阵型和 Progress，Anchor 在完成判定前收敛到命令朝向。
4. 自适应阵型把 lookahead 采样到目标之外的地图边界，误判通道变窄并让 128 人外侧槽位落出 Grid；采样距离现裁剪到剩余目标距离，目标范围内停止重排。

Stage4 Validation 新增系统顺序、配置列数、开放 Grid 最大列数、完成后额外 30 Tick 稳定性和终态朝向断言。

### 算法与性能清单结论

| 复审项 | 独立复现/指标 | 预期与决定 |
|---|---|---|
| 动态 Overlay 使静态 Corridor 失效 | 12×12、3×3 Cluster 夹具；中央 Cluster 内部动态墙不封 Portal 代表 Cell | 已修复：受影响 Cluster 从当前 Portal 重跑局部 Dijkstra，失效内部边不进入抽象候选，Stage3 通过替代 Corridor |
| Dynamic Extra Cost 的宏观选择 | 同一夹具给中央 Cluster 每 Cell `ExtraCost=100` | 已修复：动态局部边与 Portal 跨边使用统一运行时成本，宏观路线绕开高成本 Cluster |
| Flow 平滑抵消为零 | 5×5 单 Cluster，对称绕障产生东西等价后继 | 已修复：抵消时稳定回退到最小 CellIndex 的合法最优边，非目标 Cell 不再输出零方向 |
| 平滑牺牲 Bellman successor | Stage3 对每个非目标 Field Cell 校验 `neighborCost + stepCost == currentCost` | 已修复：只混合 Bellman 等价后继，最终仍投影回已验证离散边 |
| Hierarchy 最短成本/最大 Clearance 语义 | Builder 源码复核；Stage3 大体型 Portal 和普通 A* 25% 次优界夹具 | 当前边被定义为“静态成本下界 + 独立可行宽度证书”，Integration 才是逐 Cell 权威验证；未发现安全错误，不扩展为 Pareto 多边结构 |
| Path smoothing 最坏复杂度 | 源码边界为最多 `P²` 次候选直线检查，每次直线遍历受 Grid Cell 数限制；Stage2 开放/障碍/成本夹具通过 | 保留确定性最远可见贪心；它只在异步 A* 构建发生，不进入每 Tick Squad 移动。若未来长路径 P95 超预算，再改为有界窗口 |
| Cache hit Field copy/start-cost 查询 | 最终 Stage3 每轮 160 完成、140 命中、20 构建；P95 `0.0629/0.0901 ms`，最大 `0.8433/0.8997 ms` | Field copy 是 ECS Buffer 独立所有权所需，start-cost 为每请求一次 `O(F)`；当前无需增加索引表和额外内存 |
| Cache eviction | 单 Cluster 66 请求：前 65 个唯一目标，第 66 个重复第 65 个 | 已修复：第 65 项触发 64 项有界代际回收并立即写入新代，第 66 项命中；不再永久冻结首批目标或累积孤立切片 |

动态宏观边只在 `OverlayVersion > 1` 后启用；初始静态场景继续使用四个 Generation 的原快速路径。发生动态修改后，每个 Portal Node 获得独立局部 Generation，Integration 使用其后的专属 Generation，批次之间不重叠。

### 最终 Stage3 Path / Flow

共同输入：32 Ani、720 样本 Tick、Map Hash `b14adf5dd8494058a3c2b236b10f4ea94dec75802a55e549f182d193ff9886e4`、Replay Hash `6b938171c7c5765a0d42073d031e0488896cfa74c55a79587abda4ed0ac555f5`、Grid Bake Hash `4b6e63686d61726b4772696444617431`。

| 结果文件 | 提交/完成/成功/失败 | Cache/Build | Abstract/Integration | P50/P95/P99/Max ms |
|---|---|---|---|---|
| `GridNavigation_32_20260821_135204.json` | 128/160/160/0 | 140/20 | 98175/6400 | 0.0491/0.0629/0.3503/0.8433 |
| `GridNavigation_32_20260821_135402.json` | 128/160/160/0 | 140/20 | 98175/6400 | 0.0543/0.0901/0.3961/0.8997 |

除墙钟耗时和时间戳外，请求、命中、构建、成功/失败、Abstract/Integration 展开量和 Transform 零写入字段一致。

### 最终 Stage4 Squad

| 规模 | 结果文件 | 固定终止 | 首次终态 | 到达 | 结论 |
|---:|---|---:|---:|---:|---|
| 32 | `GridNavigation_32_20260821_133935.json`、`GridNavigation_32_20260821_134116.json` | 1440 | 1015 / 1015 | 32/32 | 通过 |
| 64 | `GridNavigation_64_20260821_134250.json`、`GridNavigation_64_20260821_134409.json` | 1440 | 1041 / 1041 | 64/64 | 通过 |
| 128 | `GridNavigation_128_20260821_134524.json`、`GridNavigation_128_20260821_134643.json` | 1440 | 1077 / 1077 | 128/128 | 通过 |

| 规模/轮次 | Path | Transform 写入 | 最小间距 m | 编队误差 | P50/P95/P99 ms | Alloc P95 |
|---|---|---:|---:|---:|---|---:|
| 32-1 | 4/4 | 24960 | 1.399811 | 0.000098124 | 0.7641/0.9324/1.2344 | 0 B |
| 32-2 | 4/4 | 24960 | 1.399811 | 0.000098124 | 0.7816/1.0238/1.1700 | 0 B |
| 64-1 | 4/4 | 49920 | 1.399902 | 0.000047405 | 0.9576/1.5208/1.9026 | 0 B |
| 64-2 | 4/4 | 49920 | 1.399902 | 0.000047405 | 0.8634/1.0335/2.3193 | 0 B |
| 128-1 | 4/4 | 99840 | 1.399826 | 0.000098337 | 1.0639/1.3575/4.6672 | 0 B |
| 128-2 | 4/4 | 99840 | 1.399811 | 0.000098337 | 1.0081/1.1527/4.0695 | 0 B |

三档两轮的失败标记、终止 Tick、命令数、Squad 数、Path 数、全员到达、Transform 写入、编队误差和三类 Hash 一致。128 的最小间距差 `0.000015259 m`，属于浮点调度噪声且远离碰撞边界；首次终态 Tick 三档均完全一致。六轮主线程 P95 分配均为 0 B。

### 最终验收证据

- `Logs/R6-final-stage1.log`：Stage 1 通过，返回码 0
- `Logs/R6-final-stage2.log`：Stage 2 通过，返回码 0
- `Logs/R6-review-stage3.log`：Stage 3 与新增 R6 夹具通过，返回码 0
- `Logs/R6-final-stage4.log`：Stage 4 与终态稳定性夹具通过，返回码 0
- `Logs/R6-final-stage5.log`：Stage 5 通过，返回码 0
- 六份 Squad Benchmark 日志未出现 `error CS` 或 `InvalidOperationException`
- `dotnet restore AnimarsCatcher.slnx` 成功；`dotnet build --no-restore` 为 0 错误、241 个 Unity Package/生成器既有警告
- Assembly migration audit：331/331 脚本归属程序集，0 边界违规、0 警告、0 关键问题
- Comment style：15.01%，通过；`git diff --check` 通过

R6 验收结论为通过。namespace migration 仍是独立后续事项，不属于 R6 退出条件。
