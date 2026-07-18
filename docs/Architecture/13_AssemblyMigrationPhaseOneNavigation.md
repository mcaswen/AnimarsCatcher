# Navigation 程序集试点

[返回架构总览](README.md)

> 状态：阶段一已完成
>
> 实施日期：2026-07-18
>
> 程序集：`AnimarsCatcher.Navigation`

## 1. 阶段结果

`Assets/Scripts/Anis/Navigation/Grid` 已从预定义 `Assembly-CSharp` 迁入项目第一个自有程序集。该程序集当前同时覆盖运行时数据、算法、Job、System、Authoring、Baker 和 Editor 验收工具。

第一轮保持单程序集是有意的迁移策略。现有 Baker 与编辑器烘焙工具仍有条件编译下的内部调用，在没有完成职责反转前直接拆成 Runtime、Authoring 和 Editor 会制造非法反向依赖。当前单程序集已经通过 Editor 和 Player 编译，因此这项混编作为已登记的过渡状态保留。

本阶段没有修改路径算法、烘焙规则或正式玩法行为。

## 2. 程序集配置

程序集文件位于：

```text
Assets/Scripts/Anis/Navigation/Grid/AnimarsCatcher.Navigation.asmdef
```

当前配置遵循以下约束：

- 程序集名为 `AnimarsCatcher.Navigation`
- 根命名空间提示为 `AnimarsCatcher.Animars.Navigation`
- `Auto Referenced` 保持开启，使尚在 `Assembly-CSharp` 的上层代码可以消费 Navigation 公共 API
- `Allow Unsafe Code` 关闭
- `Override References` 关闭
- 保留 Unity Engine 引用
- 程序集依赖全部使用 GUID 形式
- 只直接引用 Burst、Collections、Entities、Entities Hybrid 和 Mathematics

Navigation 源码没有引用仍位于 `Assembly-CSharp` 的项目业务类型。这个方向是阶段一能够成立的关键：预定义程序集可以依赖自定义程序集，自定义程序集不能反向依赖预定义程序集中的业务实现。

## 3. 序列化迁移

脚本 `.meta` GUID 保持不变。由于类型从 `Assembly-CSharp` 移入自定义程序集，两个已有序列化标识同步改为新程序集名：

- `SCN_GridBakeStage1.unity` 中的 `NavigationGridAuthoring`
- `SO_NavigationGrid_SCN_GridBakeStage1.asset` 中的 `NavigationGridBakeAsset`

当前标识形式为：

```text
AnimarsCatcher.Navigation::AnimarsCatcher.Animars.Navigation.Grid.<TypeName>
```

新增 `NavigationAssemblyMigrationValidation` 作为非破坏性回归入口。它只加载现有 Scene 和 Bake Asset，不重新烘焙或保存资源，并检查：

- Bake Asset 能按新类型正常加载
- Scene 中没有 Missing Script
- Scene 中只有一个 `NavigationGridAuthoring`
- Authoring 的 Bake Asset 引用没有丢失
- Authoring 与 Bake Asset 的实际程序集均为 `AnimarsCatcher.Navigation`

批处理入口：

```text
AnimarsCatcher.Animars.Navigation.Grid.Editor.NavigationAssemblyMigrationValidation.RunFromCommandLine
```

## 4. 审计门禁

`Tools/AssemblyMigrationRules.psd1` 中的 Navigation 状态已更新为 `PhaseOneImplemented`，并登记 asmdef 路径与根命名空间。

`Tools/AuditAssemblyMigration.ps1` 现在除原有脚本归属、命名空间和候选依赖检查外，还会验证：

- asmdef 文件存在且 JSON 可解析
- 程序集名与规则一致
- 根命名空间与规则一致
- `Auto Referenced` 已开启
- Unsafe、Override References 和 No Engine References 没有被错误开启
- 程序集依赖全部使用 GUID 形式

当前审计结果：

- 自有脚本 261 个，全部具有候选归属
- 已有命名空间 41 个，全局命名空间 220 个
- Navigation 脚本 20 个，全部位于合规命名空间
- Navigation 外部业务依赖 0
- 自有 asmdef 1 个
- 严重审计错误 0
- 仍有 10 个已登记的 Runtime 与 Editor 混编 Warning

这些 Warning 是后续拆分生命周期边界的输入，不属于阶段一编译失败。

## 5. 验证结果

阶段一完成了以下验证：

- 主项目 Unity Editor 完整脚本编译通过
- 固定 Grid Scene 与 Bake Asset 序列化迁移检查通过
- Stage One Grid 算法与烘焙验收通过
- Stage Two 投影、寻路、平滑、失败状态和异步 ECS 写回验收通过
- 预定义 `Assembly-CSharp` 消费者探针可以引用 `NavigationPathRequest`
- 最小 Windows Client Player 构建成功，错误数为 0
- 最小 Windows Dedicated Server Player 构建成功，错误数为 0
- Server 与 Local 系统列表包含 `NavigationGridPathfindingSystem`
- Client 系统列表不包含 `NavigationGridPathfindingSystem`

Stage Two 仍使用真实 `World` 创建并更新路径系统。新增的系统过滤门禁同时验证自动发现范围，防止 Client World 意外创建服务器路径系统。

Player 构建验证聚焦 Navigation 程序集的 Player 兼容性和 Editor API 隔离，不替代正式游戏 Build Profile、联机流程和性能基准验收。后续迁移高层模块时仍需执行完整 Client 与 Dedicated Server 构建。

## 6. 当前边界

阶段一建立的是第一个编译期边界，不代表整个项目已经完成程序集化。

当前可以依赖的事实是：

- Navigation 内部实现由 `AnimarsCatcher.Navigation` 编译
- 其他项目代码仍主要位于 `Assembly-CSharp`
- Navigation 不依赖其他项目业务实现
- `Assembly-CSharp` 可以通过 Auto Referenced 使用 Navigation 公共 API
- Editor 工具暂时与 Runtime 同程序集，但 Player 编译不会包含 `UnityEditor` 代码

当前不能假设：

- Gameplay、Player、Networking 和 Presentation 已形成编译边界
- 当前候选程序集之间的循环依赖已经解决
- Navigation 已完成 Runtime、Authoring、Editor 和 Tests 最终拆分
- 最小 Player 构建等同于完整游戏发布构建

## 7. 后续工作

Core 与 Contracts 阶段已经完成，结果见 [Core 与 Gameplay Contracts 迁移](14_AssemblyMigrationPhaseTwoCoreContracts.md)。

优先处理顺序：

1. 核实 Anis、Base、Health、Resource 和 Global 的具体双向调用
2. 区分共享数据契约与具体 System 实现
3. 为准备迁移的类型补齐命名空间并检查序列化身份
4. 只创建能够保持单向依赖的最小 Core 或 Contracts 边界
5. 重复执行审计、Editor 编译、Player 构建和序列化检查

Navigation 自身的 Runtime、Authoring 和 Editor 拆分不在下一步立即执行。应先消除 Baker 对 Editor 实现的条件调用，并为 Editor 与 Tests 建立明确入口，再进入最终拆分。
