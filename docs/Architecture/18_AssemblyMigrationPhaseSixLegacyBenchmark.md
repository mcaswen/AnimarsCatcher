# Legacy Benchmark 程序集迁移

[返回架构总览](README.md)

> 状态：阶段六已完成
>
> 实施日期：2026-07-19
>
> 新增程序集：`AnimarsCatcher.Benchmarks.LegacyNavigation`

## 1. 阶段结果

阶段六把 `Assets/Scripts/Benchmarks/LegacyNavMesh` 下的 24 个脚本迁入独立的 `AnimarsCatcher.Benchmarks.LegacyNavigation` 程序集。

该程序集保存旧移动性能基线：

- 旧 Movement FSM 与 Planner
- 固定矩形阵型
- 服务端移动命令消费者
- 逐 Ani NavMesh 路径规划与跟随
- 旧 Physics 探测和移动
- 依赖 NavMesh 的资源搬运适配

迁移只建立编译边界，没有优化算法、修改 System 顺序、改变 Ghost 字段或切换正式移动后端。

## 2. 依赖方向

Legacy Benchmark 当前只依赖以下项目程序集：

- `AnimarsCatcher.Core`
- `AnimarsCatcher.Gameplay.Contracts`
- `AnimarsCatcher.Gameplay`
- `AnimarsCatcher.Player`

这些依赖用于复用 FSM 基础设施、玩法状态、移动请求契约和角色标识。Benchmark 不依赖 Navigation、Networking 或 Presentation。

Core、Gameplay、Navigation、Player、Networking 和 Presentation 都不引用 Benchmark。程序集审计把该方向固定为正式模块可以被 Benchmark 消费，但正式模块不能从 Legacy 获取类型或流程实现。

## 3. 命名空间与序列化

24 个脚本统一使用 `AnimarsCatcher.Benchmarks.LegacyNavigation`。阶段六开始前只有 2 个脚本已经迁移，另外 22 个仍在全局命名空间。

命名空间迁移后：

- Legacy 范围命名空间覆盖率为 100%
- 全项目全局命名空间脚本从 28 个降到 6 个
- 原脚本路径和 `.meta` GUID 保持不变
- Ghost Component、Baker 和 System 类型继续由 Unity 与 NetCode Source Generator 发现

阶段六移除了 `AniMovementFsmBaker` 中未使用的 `Unity.VisualScripting` 引用，避免为了一个无效 using 给 Benchmark 增加额外包依赖。

## 4. 当前运行事实

Legacy 已经完成程序集隔离，但还没有完成运行时后端隔离。

当前正式 Prefab 仍保留以下 Legacy Authoring：

- Picker 与 Blaster Ani Prefab 包含 `AniMovementFsmAuthoring`
- Picker 与 Blaster Ani Prefab 包含 `NavAgentAuthoring`
- Picker 与 Blaster Ani Prefab 包含 `AniPhysicsAuthoring`
- 多个可拾取资源 Prefab 包含 `NavAgentAuthoring`

因此，独立 asmdef 不能被直接禁用。正式 Grid 后端切换、Benchmark Prefab 拆分和后端互斥属于 Grid 移动阶段零与阶段七，不在本次程序集迁移中提前实现。

这里的隔离含义是：编译器可以阻止正式程序集引用 Legacy，但不会改变当前正式场景仍使用旧移动链路的事实。

## 5. 程序集配置

Benchmark asmdef 显式引用实际使用的 DOTS 包：

- Burst、Collections、Entities 与 Entities Hybrid
- Mathematics 与 Transforms
- NetCode
- Unity Physics 与 Physics Hybrid

`UnityEngine.AI.NavMesh` 属于 Unity Engine 模块，不需要额外引用 `Unity.AI.Navigation` 包程序集。Benchmark asmdef 保持 `Auto Referenced` 开启，因为正式 Prefab 当前仍需要其中的 Authoring 和 Baker。

程序集使用固定 GUID，允许其他验证工具稳定确认类型归属。所有项目程序集引用都使用 GUID 形式。

## 6. 自动验收

阶段六新增 `AssemblyMigrationStageSixValidation`，检查：

- FSM Authoring、Nav Authoring、Physics Authoring 和关键 System 位于 Benchmark 程序集
- Picker 与 Blaster Prefab 仍保留三个 Legacy Authoring
- 资源 Prefab 仍保留 `NavAgentAuthoring`
- 阶段五 Presentation 验收继续通过
- 全部 Scene 与 Prefab 没有 Missing Script

Unity 物理副本验证结果：

- 完整导入和脚本编译通过
- Benchmark NetCode Ghost 代码生成通过
- `AnimarsCatcher.Benchmarks.LegacyNavigation.dll` 正常生成
- 阶段六自动验收通过
- Windows Client 构建成功
- Windows Dedicated Server 构建成功

批处理验证没有执行实际 NavMesh 性能采样。固定命令回放、32/64/128 Ani 场景和 P50/P95/P99 数据仍属于独立 Benchmark Harness 工作。

## 7. 审计结果

阶段六完成后：

- 自有脚本为 269 个
- `Assets/Scripts` 下项目程序集定义为 9 个
- 项目 asmref 为 8 个
- Legacy Benchmark 24 个脚本命名空间覆盖率为 100%
- 全项目剩余全局命名空间脚本为 6 个
- 直接双向依赖为 0
- 程序集依赖边界违规为 0
- 严重审计问题为 0
- 项目总注释率为 17.28%

剩余 10 条审计 Warning 都来自既有 Navigation 或 Networking 的 Runtime 与 Editor 条件编译混合文件，不属于 Benchmark 阶段新增问题。

## 8. 后续工作

下一阶段是最终依赖收紧。

优先处理：

1. 迁移剩余 Editor、Physics 和 Terrain 脚本
2. 检查 Runtime、Editor 和 Authoring 的最终编译边界
3. 复核所有 asmdef 的 Auto Referenced 和未使用包引用
4. 判断现有 asmref 是长期多目录归属还是仅迁移期过渡配置
5. 生成最终程序集依赖图并更新事实架构文档

Legacy 运行时后端互斥、Benchmark Harness 和正式 Grid 切换继续按 Grid 移动计划执行，不与程序集阶段七混为同一项改动。
