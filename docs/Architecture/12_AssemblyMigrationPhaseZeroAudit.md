# 程序集迁移阶段零审计

[返回架构总览](README.md)

> 状态：阶段零已完成；本文保留迁移前快照，阶段一结果见 [Navigation 程序集试点](13_AssemblyMigrationPhaseOneNavigation.md)
>
> 审计基线 Commit：`8723acb33d4527c44128bc89088d0ea4012c108e`
>
> 审计日期：2026-07-18

## 1. 阶段零交付物

阶段零新增三个可重复使用的工具文件：

- `Tools/AssemblyMigrationRules.psd1`：按路径前缀定义脚本负责人、候选程序集、生命周期和命名空间要求
- `Tools/AuditAssemblyMigration.ps1`：检查脚本归属、命名空间、生命周期、候选跨模块依赖和直接双向依赖
- `Tools/GlobalNamespaceBaseline.txt`：冻结现有 220 个全局命名空间脚本，阻止新增全局命名空间业务代码

运行命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Tools\AuditAssemblyMigration.ps1 `
  -JsonOutputPath Temp\AssemblyMigrationAudit.json
```

JSON 报告写入 `Temp`，用于本地检查和后续工具处理，不作为手写文档提交。归属规则和本审计文档是需要维护的事实基线。

全局命名空间基线只允许删除条目，不允许为新脚本增加条目。脚本完成命名空间迁移后必须同步从基线移除；新增未声明命名空间的脚本会使审计直接失败。

依赖分析通过唯一项目类型名和源文件标识符进行启发式匹配。它可以发现大部分直接引用，但不能替代 Roslyn 语义分析或实际 asmdef 编译。报告中的依赖和循环必须在迁移对应模块前人工确认。

## 2. 编译与版本基线

当前主要版本：

- Unity `6000.2.7f2`
- Entities `1.4.3`
- NetCode `1.9.0`
- Burst `1.8.25`

阶段零执行：

```powershell
dotnet build Assembly-CSharp-Editor.csproj --no-restore
```

结果为零 Error、227 个 Warning。Warning 主要来自 Unity Package 的过时 API 和未赋值字段，以及直接使用外部 `dotnet` 编译时的 NetCode Source Generator 模板路径问题。

项目自有代码中仍可见的已知 Warning 为：

- `StartServerListenSystem.cs` 存在不可达代码 `CS0162`
- NetCode Source Generator 在外部 `dotnet build` 环境报告 `CS8785`

这些 Warning 已作为迁移前基线记录，不属于阶段零新增回归。后续 asmdef 阶段必须继续保持零新增 Error，并区分 Unity Editor 真实编译与外部 `dotnet` 环境差异。

当前仓库没有可直接调用的 Client 和 Dedicated Server 自动构建入口。该缺口不阻塞依赖审计完成，但在 Navigation asmdef 退出前必须补充或执行可重复的 Player Build 验证。

## 3. 脚本归属结果

审计覆盖 `Assets/Scripts` 下全部 260 个自有 C# 脚本：

- 已分配候选程序集：260
- 未分配脚本：0
- 已有命名空间：40
- 仍在全局命名空间：220
- Navigation 外部业务依赖：0
- 过期全局命名空间基线条目：0
- 严重审计错误：0

候选程序集归属如下：

- `AnimarsCatcher.Navigation`：19 个脚本，状态为 `PilotReady`
- `AnimarsCatcher.Animars`：42 个脚本，等待提取共享契约
- `AnimarsCatcher.Player`：44 个脚本，等待提取共享契约
- `AnimarsCatcher.Networking`：29 个脚本，等待提取共享契约
- `AnimarsCatcher.Resource`：25 个脚本，等待提取共享契约
- `AnimarsCatcher.Benchmarks.LegacyNavigation`：22 个脚本，等待与正式运行时隔离
- `AnimarsCatcher.Mono`：35 个脚本，等待解除 Runtime 反向依赖
- `AnimarsCatcher.UI`：14 个脚本，等待解除 Runtime 反向依赖
- `AnimarsCatcher.Global`：8 个脚本，等待提取共享契约
- `AnimarsCatcher.Base`：5 个脚本，等待依赖审计
- `AnimarsCatcher.Camp`：5 个脚本，等待提取共享契约
- `AnimarsCatcher.Health`：5 个脚本，等待提取共享契约
- `AnimarsCatcher.Editor`：3 个脚本，等待运行时依赖迁移
- `AnimarsCatcher.Physics`：2 个脚本，等待 Authoring 依赖审计
- `AnimarsCatcher.Terrain`：2 个脚本，等待 Authoring 依赖审计

这些名称是阶段零候选边界，不代表已经批准创建对应 asmdef。每条规则都可以在实际语义依赖确认后调整，但任何新增脚本必须被规则覆盖。

## 4. 命名空间现状

当前 40 个已有命名空间的脚本主要来自：

- Navigation：19 个脚本全部使用 `AnimarsCatcher.Animars.Navigation`
- MonoBehaviour：20 个脚本使用 `AnimarsCatcher.Mono`
- Player Editor：1 个脚本使用兼容的 Unity Character Controller Editor 命名空间

其余 220 个脚本仍在全局命名空间。阶段零不批量修改这些脚本，因为命名空间变化可能影响 Scene、Prefab、ScriptableObject、`SerializeReference`、Ghost 和外部字符串类型名。

处理顺序为：

1. 先确认脚本候选程序集和跨模块调用
2. 再按模块迁移命名空间并处理序列化身份
3. 最后创建该模块 asmdef

从阶段零开始禁止新增全局命名空间业务脚本。

## 5. Runtime 与 Editor 风险

审计识别到 10 个需要在拆分 Runtime 和 Editor 前处理的文件。

Navigation 风险：

- `NavigationGridAuthoring.cs` 同时包含 Runtime Authoring 和 Editor 条件逻辑
- `NavigationGridBakeAsset.cs` 同时包含 Runtime 资产数据和 Editor 条件逻辑
- `NavigationGridBaker.cs` 在 Editor 条件下调用 Editor 命名空间工具
- `NavigationGridBakeUtility.cs` 是 Editor-only 文件，但物理位置仍在 `Baking` 目录

其他模块风险：

- `NetworkRunRole.cs` 包含 Editor 条件逻辑
- `CustomBootstrap.cs` 包含 Editor 条件逻辑
- `StartClientConnectSystem.cs` 包含 Editor 条件逻辑
- `StartServerListenSystem.cs` 包含 Editor 条件逻辑
- `ClientGoInGameSystem.cs` 包含 Editor 条件逻辑
- `ServerGoInGameDebugSystem.cs` 包含 Editor 条件逻辑

这些文件当前依靠 `#if UNITY_EDITOR` 在 `Assembly-CSharp` 中工作。创建独立 Runtime 和 Editor asmdef 时，必须把 Editor 状态采集、菜单、调试入口和新鲜度检查移到单向依赖的 Editor 边界。

## 6. 候选跨模块依赖

工具识别到 42 条候选跨模块依赖和 12 组直接双向依赖。

Gameplay 相关候选双向依赖：

- `AnimarsCatcher.Animars` 与 `AnimarsCatcher.Base`
- `AnimarsCatcher.Animars` 与 `AnimarsCatcher.Health`
- `AnimarsCatcher.Animars` 与 `AnimarsCatcher.Player`
- `AnimarsCatcher.Animars` 与 `AnimarsCatcher.Resource`
- `AnimarsCatcher.Base` 与 `AnimarsCatcher.Global`
- `AnimarsCatcher.Base` 与 `AnimarsCatcher.Health`
- `AnimarsCatcher.Health` 与 `AnimarsCatcher.Resource`

Legacy Benchmark 隔离风险：

- `AnimarsCatcher.Animars` 与 `AnimarsCatcher.Benchmarks.LegacyNavigation`
- `AnimarsCatcher.Benchmarks.LegacyNavigation` 与 `AnimarsCatcher.Resource`

这说明当前正式 Ani 和 Resource 逻辑仍直接引用部分 Legacy Navigation 类型。创建正式 Gameplay asmdef 前，必须先明确旧移动后端的输入契约和后端选择边界。

表现与网络候选双向依赖：

- `AnimarsCatcher.Mono` 与 `AnimarsCatcher.Networking`
- `AnimarsCatcher.Mono` 与 `AnimarsCatcher.Resource`
- `AnimarsCatcher.Mono` 与 `AnimarsCatcher.UI`

Mono 和 Networking 的候选引用包含连接控制、World 管理、加载 UI、运行角色和 UI 事件桥接。迁移时应把运行角色、连接请求和 UI 通知数据提取为契约，而不是让 Networking 调用具体 Mono UI。

依赖工具按类型名进行启发式匹配，`PlayerInput`、`State` 等名称仍可能产生误报。每个候选循环在重构前必须回到具体文件和调用链确认。

## 7. Navigation 试点结论

Navigation 当前满足第一批单程序集试点的主要条件：

- 19 个脚本全部有统一命名空间
- 没有推断出的外部项目业务依赖
- Stage One 和 Stage Two 已有自动验收入口
- Grid Scene 和 Bake Asset 已有固定序列化基线
- 当前 Editor 编译通过

Navigation 仍有四个 Editor 边界风险，因此阶段一建议先创建覆盖整个 Grid 模块的单程序集，不立即拆 Runtime、Authoring 和 Editor。

在创建 `AnimarsCatcher.Navigation.asmdef` 前必须再次确认：

- `NavigationGridBaker` 对 `NavigationGridBakeUtility` 的引用在单程序集内可编译
- Player Build 不包含非法 UnityEditor 引用
- Stage One、Stage Two、固定 Scene 和 Bake Asset 均通过
- asmdef 引用只包含实际使用的 Unity Package

## 8. 阶段零决策

根据审计结果，后续执行遵循以下决策：

1. 第一批只迁移 Navigation，不同时创建 Gameplay、Player 或 Networking asmdef
2. Navigation 第一轮使用单程序集，Editor 最终拆分留到依赖反转完成后
3. Gameplay asmdef 创建前先处理 Anis、Base、Health、Resource 和 Global 的共享契约
4. Legacy Benchmark 必须先解除正式模块对其实现类型的依赖
5. Mono、UI 和 Networking 在双向调用解除前不创建独立正式边界
6. 新增脚本必须更新归属规则并使用合规命名空间
7. 工具发现的新双向依赖必须进入 Code Review

## 9. 阶段零退出检查

已完成：

- 全部 260 个脚本具有候选程序集或明确待处理状态
- Navigation 没有未记录的外部业务依赖
- 已记录全局命名空间规模
- 已记录 Runtime 与 Editor 混编风险
- 已记录候选跨模块依赖和直接双向依赖
- Editor C# 基线编译为零 Error
- 审计工具可以重复生成相同结构的结果
- 阶段零不包含玩法行为修改

已登记但不阻塞阶段零的后续工作：

- 增加可重复的 Client 与 Dedicated Server Player Build 入口
- 使用实际 asmdef 编译验证启发式依赖结果
- 为非 Navigation 模块逐步迁移命名空间
- 处理现有项目 Warning 基线

阶段零完成后的 Navigation 单程序集试点已经实施，当前结果见 [Navigation 程序集试点](13_AssemblyMigrationPhaseOneNavigation.md)。
