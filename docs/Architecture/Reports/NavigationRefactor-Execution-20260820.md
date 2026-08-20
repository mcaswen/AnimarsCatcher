# Navigation 重构执行报告（2026-08-20）

## 结论

R1-R5 的职责拆分、目录迁移和程序集边界已经落地。当前 `Assets/Scripts/Navigation` 共 57 个 C# 文件，其中 Grid 32、Squad 8、Tooling 17。R6 尚未完成：最终 Stage4 Squad 基准存在到达率为 0 和终态样本数漂移，不能宣称完整行为确定性门禁已通过。

本轮未执行 namespace migration，也未修改 A*、HPA、Flow Direction、成本公式、缓存键或 Benchmark 指标定义。

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
- `CheckCommentStyle.ps1`：通过项目 15% 门槛
- `.meta` GUID：无重复；脚本均有 `.meta`；未发现 `m_Script: {fileID: 0}`
- 旧类引用：`NavigationGridPathAlgorithms`、`NavigationGridFlowFieldAlgorithms`、`AniSquadMovementAlgorithms`、`NavigationGridRuntimeRules`、`NavigationGridAlgorithms` 均为 0
- 异步 Job：Path/Flow 句柄登记到 `SystemState.Dependency`；最终四轮 Benchmark 日志的 `InvalidOperationException` 均为 0

持久日志位于 `Logs/NavigationRefactor/`。该目录和 `BenchmarkResults/` 属于本机验证产物，不作为源码提交内容。

## 最终 Benchmark

共同输入：32 Ani、Git `11d63d2`、Map Hash `b14adf5dd8494058a3c2b236b10f4ea94dec75802a55e549f182d193ff9886e4`、Replay Hash `6b938171c7c5765a0d42073d031e0488896cfa74c55a79587abda4ed0ac555f5`、Grid Bake Hash `4b6e63686d61726b4772696444617431`。

### Stage3 Path / Flow

| 结果文件 | 提交/完成/成功/失败 | Cache/Build | Abstract/Integration | P50/P95/P99 ms |
|---|---|---|---|---|
| `GridNavigation_32_20260820_112609.json` | 128/160/160/0 | 140/20 | 98175/6400 | 0.0523/0.0722/0.3518 |
| `GridNavigation_32_20260820_112709.json` | 128/160/160/0 | 140/20 | 98175/6400 | 0.0507/0.0703/0.3661 |

除耗时序列和时间戳外，路径与 Field 的确定性字段一致。

### Stage4 Squad

| 结果文件 | Path | 到达 | Transform 写入 | 最小间距 | 编队误差 | P50/P95/P99 ms |
|---|---|---:|---:|---:|---:|---|
| `GridNavigation_32_20260820_112829.json` | 4/4 | 0/32 | 13440 | 1.379229 | 15.824133 | 0.8694/1.8758/3.5880 |
| `GridNavigation_32_20260820_112929.json` | 4/4 | 0/32 | 14752 | 1.378407 | 15.825360 | 0.8769/1.9652/3.8468 |

两轮路径结果一致，但 Transform 写入相差 1312，间距和编队误差也有漂移。两轮到达率均为 0。该结果登记为 R6：先确认 Benchmark 是否按固定 Server Tick 终止，再判断是否存在 Squad 运动或到达判定问题。

## R6 待办

1. 固定 Benchmark 终止 Tick，记录 Anchor、槽位、Path State 和到达判定序列。
2. 分离“基准停止时机漂移”和“Squad 模拟非确定性”。
3. 明确 720 个采样 Tick 的预期到达率，再为算法修复建立独立任务。
4. 结论稳定后运行 32/64/128 规模回归，并单独评估 namespace migration。
