# Core 与 Gameplay Contracts 迁移

[返回架构总览](README.md)

> 状态：阶段二已完成
>
> 实施日期：2026-07-18
>
> 新增程序集：`AnimarsCatcher.Core`、`AnimarsCatcher.Gameplay.Contracts`

## 1. 阶段结果

阶段二建立了两个稳定的底层编译边界，并把跨 Gameplay 模块共享的数据从具体实现目录中抽离。具体 System、Authoring、Baker 和流程策略仍留在原模块，通过显式命名空间引用消费这些底层类型。

本阶段没有为 Anis、Base、Camp、Global、Health 或 Resource 创建领域 asmdef。它们之间仍存在少量实现耦合，继续强拆只会把 System 对 System 的引用变成编译循环。

当前项目自有程序集为：

- `AnimarsCatcher.Core`
- `AnimarsCatcher.Gameplay.Contracts`
- `AnimarsCatcher.Navigation`

## 2. Core 边界

Core 位于：

```text
Assets/Scripts/Core
```

当前只包含三个通用 FSM 文件：

- `FsmBase.cs`：状态、条件、动作标识符以及实体状态机上下文
- `FsmBlackboardData.cs`：可同步黑板数据与类型安全读写方法
- `FsmGraph.cs`：不可变状态图 Blob 数据

这些类型统一使用 `AnimarsCatcher.Core.Fsm` 命名空间。Core 不包含 Ani 移动 ID 区间、业务事件、Registry 生命周期或具体运行 System。

`FsmIdSpace` 和 `FsmEventBus` 已移出 Core：

- `FsmIdSpace` 属于 `AnimarsCatcher.Animars.Fsm`，保存 Ani 业务标识符区间
- `FsmEventBus` 属于 `AnimarsCatcher.Animars.Fsm`，保存当前 Ani 分配事件语义
- `FsmRegistry`、Bootstrap 和三个运行 System 暂时仍位于 `Assembly-CSharp`

Core 只直接引用 Burst、Collections、Entities、Mathematics 和 NetCode，不引用任何项目业务程序集。

## 3. Gameplay Contracts 边界

Gameplay Contracts 位于：

```text
Assets/Scripts/Gameplay/Contracts
```

所有类型统一使用 `AnimarsCatcher.Gameplay.Contracts` 命名空间。当前收纳的契约包括：

- Ani 类型标签、编队状态和可攻击目标标签
- Base 标签与世界空间 AABB
- Camp、CampType、PlayerCamp 和 LocalPlayerCamp
- Health 与 DamageEvent
- GameResult 与 GameOverRpc
- ResourceItemKind、FragileCrystal 和资源攻击标签
- ResourceCarryAssignment、AniCarryResourceOrder、命令锁和拾取请求

Contracts 不包含具体 System、MonoBehaviour、Authoring、Baker、静态运行角色或 UI 实现。

它直接引用 Entities、Mathematics 和 NetCode。由于 NetCode 为 Ghost 契约生成 Serializer，程序集还必须直接引用 Burst 与 Collections；这两项引用服务于生成代码，不表示 Contracts 承担运行流程。

## 4. 文件迁移与兼容

迁移文件与对应 `.meta` 一起移动，原脚本 GUID 保持不变。具体调用方只增加了显式 `using`，没有复制类型或保留双份兼容声明。

纯 ECS Component、Tag、Buffer 和 RPC 的程序集与命名空间发生了变化，因此 Client 与 Server 必须使用同一提交重新生成 Ghost Serializer 和 Entity Scene。项目不支持阶段二之前的 Client 与阶段二之后的 Server 混用。

Authoring 和 MonoBehaviour 仍位于 `Assembly-CSharp`，所以已有 Scene 与 Prefab 的 `m_EditorClassIdentifier` 不需要改成新程序集名。完整资产副本已完成 Scene、Prefab、SubScene 和 Bake Asset 导入验证。

## 5. 依赖变化

迁移前审计记录了 42 条候选跨模块依赖和 12 组直接双向依赖。阶段二完成后：

- 候选跨模块依赖为 39 条
- 直接双向依赖降为 6 组
- Core 禁止依赖任何项目业务程序集
- Gameplay Contracts 只允许依赖 Core
- Navigation 只允许依赖 Core 与 Gameplay Contracts
- 当前边界违规为 0

Base、Camp、Global、Health 和 Resource 之间由共享 Component 造成的循环已经从候选图中消失。

剩余 6 组双向依赖为：

- Anis 与 Legacy Navigation
- Anis 与 Player
- Legacy Navigation 与 Resource
- Mono 与 Networking
- Mono 与 Resource
- Mono 与 UI

这些依赖主要来自具体 System、Mono 桥接和 Legacy 实现，不应继续通过扩大 Contracts 解决。

## 6. 审计门禁

`Tools/AssemblyMigrationRules.psd1` 已登记两个新程序集、根命名空间和允许依赖范围。

`Tools/AuditAssemblyMigration.ps1` 新增通用依赖边界检查。被标记为强制边界的程序集一旦引用未允许的项目程序集，审计会直接失败。

当前审计结果：

- 自有脚本 262 个，全部具有候选归属
- 已有命名空间 56 个
- 全局命名空间 206 个
- 自有 asmdef 3 个
- 依赖边界违规 0
- 严重审计错误 0
- 仍有 10 个已登记的 Runtime 与 Editor 混编 Warning

## 7. 验证结果

阶段二使用完整项目的物理副本验证，没有使用目录联接或共享源码目录。

已通过：

- 全部 Assets 和 ProjectSettings 的 Unity Editor 导入与脚本编译
- NetCode Ghost Serializer 和 Entities 代码生成
- Navigation Stage Two 自动验收
- 固定 Grid Scene 与 Bake Asset 序列化检查
- Build Settings 中三个真实场景的 Windows Client 构建
- Build Settings 中三个真实场景的 Windows Dedicated Server 构建
- Client BuildReport 为 `Succeeded`，错误数为 0
- Server BuildReport 为 `Succeeded`，错误数为 0
- 程序集迁移审计无严重问题

使用 `-nographics` 构建真实场景时，Client 会因 Global Illumination 缺少图形设备记录环境错误。最终验收保留图形设备运行 batchmode，Client 与 Server 均为零错误。

## 8. 后续工作

Gameplay 迁移已完成，具体结果见 [Gameplay 程序集迁移](15_AssemblyMigrationPhaseThreeGameplay.md)。阶段三没有直接把所有领域目录各自加上 asmdef，而是先清理实现级反向依赖，再合并到同一 Gameplay 编译边界。

阶段三实际处理顺序：

1. 解除 Health 对 `AniAttackTargetCleanupSystem` 的具体更新顺序引用
2. 解除 Anis 对 `ServerCampAssignmentPolicy` 的静态策略引用
3. 划分 Anis、Player 与 Legacy Navigation 的命令和表现边界
4. 解除 Resource 对 Legacy Navigation 具体移动组件的依赖
5. 根据修正后的依赖图决定 Gameplay 合并还是分领域程序集

Mono、Networking、Resource 和 UI 的桥接循环留到对应迁移阶段处理，不进入 Core 或 Gameplay Contracts。
