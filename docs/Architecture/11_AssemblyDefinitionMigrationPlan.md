# 程序集定义迁移前置计划

[返回架构总览](README.md)

> 状态：阶段零至阶段七实现完成；当前 331 个脚本、15 个 asmdef、0 个 asmref 的归属审计通过，实施结果见 [程序集迁移实施与最终收紧](12_AssemblyMigrationPhaseSevenFinalTightening.md)
>
> 第一批试点模块当前位于：`Assets/Scripts/Navigation`
>
> 本计划只处理程序集边界和必要的前置迁移，不改变玩法行为

阶段零至阶段七的审计基线、边界决策、序列化处理和验收结果统一见 [程序集迁移实施与最终收紧](12_AssemblyMigrationPhaseSevenFinalTightening.md)。

## 1. 目标

这次迁移的目标不是单纯减少 `Assembly-CSharp` 的脚本数量，而是建立可以由编译器检查的模块边界。

完成后应当得到以下结果：

- 修改上层 UI 或 MonoBehaviour 时，不重新编译稳定的底层导航算法
- Runtime、Editor、Tests 和 Benchmark 具有明确的单向依赖
- Gameplay、Player、Networking 和 Presentation 之间不存在循环引用
- 公共数据契约与具体 System 实现分离
- 正式代码不能引用 Legacy Benchmark
- 新增模块时可以明确判断应该依赖哪个程序集
- Unity Scene、Prefab、ScriptableObject、SubScene 和 Ghost 数据仍能正常加载

这次迁移不以程序集数量越多越好。不会为每个 `Algorithms`、`Components` 或 `Systems` 小目录单独创建程序集，也不会为了通过编译把所有类型改成 `public`。

## 2. 完成状态

当前仓库已经达到以下状态：

- `Assets/Scripts` 有 331 个 C# 文件、15 个项目 `.asmdef` 和 0 个项目 `.asmref`
- 全部脚本具有明确命名空间和程序集归属，全局命名空间脚本为 0
- 项目业务脚本不再编译到预定义 `Assembly-CSharp`
- Navigation、Networking 和 Player 均具有独立 Editor-only 程序集，Navigation 还拆出独立 Validation 与 Benchmark 程序集
- Physics 与 Terrain Authoring 统一进入 `AnimarsCatcher.Physics.Authoring`
- 全部项目 asmdef 关闭 `Auto Referenced`
- 项目 asmref 已全部移除，目录归属由各模块根目录的 asmdef 直接覆盖
- 直接双向依赖、边界违规和迁移审计 Warning 均为 0

最终程序集清单、实际依赖图和外部 Sample 边界见阶段七实施文档。

## 3. 目标依赖方向

最终依赖方向建议保持为：

```text
Core
  -> Gameplay.Contracts
      -> Navigation
      -> Gameplay
      -> Player
      -> Networking
          -> Presentation

Gameplay / Navigation
  -> Benchmarks.LegacyNavigation

Runtime
  -> Editor

Runtime
  -> Tests
```

图中的箭头表示“被上层依赖”。实际规则如下：

- `Core` 不依赖任何项目业务程序集
- `Gameplay.Contracts` 只依赖 Core 和必要 Unity Package
- Navigation、Gameplay、Player 和 Networking 可以依赖 Core 与共享契约
- Presentation 可以依赖运行时业务模块，运行时业务模块不能反向依赖 Presentation
- Editor 只能依赖对应 Runtime 或 Authoring 程序集
- Tests 只能依赖被测试程序集和测试框架
- Benchmarks 可以依赖正式模块，正式模块不能依赖 Benchmarks

## 4. 建议程序集边界

下面是目标边界，不要求第一轮全部创建。

### 4.1 AnimarsCatcher.Core

只保存稳定且没有玩法含义的底层能力，例如通用数据结构、数学辅助和无场景依赖的基础工具。

禁止把暂时无法归类的代码放入 Core。高频变化的 Gameplay 数据也不进入 Core，否则任何字段变化都会触发所有上层程序集重编译。

### 4.2 AnimarsCatcher.Gameplay.Contracts

保存确实需要被多个运行时模块共享的数据契约：

- ECS Component、Buffer 和 Tag
- Command、RPC 和跨模块请求数据
- 稳定枚举和只读接口
- 不包含流程控制的配置数据定义

该程序集尽量不包含 System、MonoBehaviour、Editor 工具或具体业务服务。

### 4.3 AnimarsCatcher.Navigation

第一批先让整个 `Assets/Scripts/Navigation/Grid` 使用一个程序集，覆盖：

- Grid Runtime 数据
- 烘焙数据与 Authoring
- 路径算法、Job 和 System
- 当前 Editor 工具和自动验收入口

单程序集试点稳定后，再评估拆分：

```text
AnimarsCatcher.Navigation.Runtime
AnimarsCatcher.Navigation.Authoring
AnimarsCatcher.Navigation.Editor
AnimarsCatcher.Navigation.Tests
```

迁移规划时没有直接拆成四个程序集，因为当时 `NavigationGridBaker` 会在 Editor 下调用 `NavigationGridBakeUtility`。必须先消除 Runtime 或 Authoring 对 Editor 实现的反向依赖。

当前最终边界没有照搬上述候选名称，而是形成四个程序集：`AnimarsCatcher.Navigation` 承载 Runtime、Authoring 和 Baker，`AnimarsCatcher.Navigation.Editor` 承载编辑器工具，`AnimarsCatcher.Navigation.Validation` 承载自动夹具，`AnimarsCatcher.Navigation.Benchmark` 承载 Grid 工作负载。这个结果以实际生命周期和依赖方向为准。

### 4.4 Gameplay 相关程序集

`Anis`、`Base`、`Camp`、`Health`、`Resource` 和 `Global` 是否合并为一个 Gameplay 程序集，需要以依赖审计结果决定。

如果这些模块之间存在大量双向引用，应先提取共享契约，再选择以下一种方案：

- 依赖关系可以单向化时，保留多个领域程序集
- 生命周期和数据所有权高度一致时，合并为 `AnimarsCatcher.Gameplay`

不能使用 `.asmref` 把循环依赖隐藏在多个目录中。

### 4.5 Player 与 Networking

Player 保存输入、预测、KCC 和相机相关运行时逻辑。Networking 保存 World 创建、连接、监听、大厅、InGame、Spawn 和网络生命周期。

两者之间如果出现双向引用，应把双方共享的 Command、RPC、连接状态和 Spawn 契约移入 `Gameplay.Contracts` 或独立 Protocol 契约程序集，不允许互相引用具体 System。

### 4.6 Presentation

UI、GameObject View、音频、菜单和 Mono 桥接属于表现层。

根据物理目录和依赖审计结果，可以拆为 `AnimarsCatcher.UI` 与 `AnimarsCatcher.Mono`，也可以在完成目录整理后合并为 `AnimarsCatcher.Presentation`。无论采用哪种方式，Gameplay 和 Networking 都不能反向调用具体 UI 类型。

### 4.7 Legacy Benchmark

`Assets/Scripts/Benchmarks/LegacyNavMesh` 最终使用独立程序集，例如：

```text
AnimarsCatcher.Benchmarks.LegacyNavigation
```

该程序集保持可执行，用于性能对比，但不能成为正式功能的依赖源。

## 5. 创建 asmdef 前的必要迁移

### 5.1 建立脚本归属清单

为每个自有脚本确认：

- 当前目录和命名空间
- 所属模块负责人
- 预期程序集
- 是否运行在 Client、Server、Local、Editor 或多个环境
- 直接引用的项目类型
- 直接引用的 Unity Package
- 是否参与 Scene、Prefab、ScriptableObject、Ghost 或 SubScene 序列化
- 是否为 Authoring、Baker、System、MonoBehaviour 或纯算法

清单的重点是找到跨模块依赖和序列化风险，不要求把所有 Unity API 使用逐条登记。

### 5.2 清理全局命名空间

迁移到 asmdef 前，目标模块内的脚本必须具有稳定命名空间。

命名空间应与逻辑目录一致，但不要求把 `Algorithms`、`Components`、`Systems` 等实现目录全部加入命名空间。程序集边界表达模块，命名空间表达 API 归属，两者不需要机械地一一对应。

第一批 Navigation 已满足统一命名空间要求。后续模块不得在迁移过程中继续新增全局命名空间类型。

### 5.3 审计访问级别

程序集拆分后，默认或 `internal` 类型不能被其他程序集访问。

处理原则：

- 真正的跨模块契约才改为 `public`
- 只被同模块使用的类型保持 `internal` 或默认访问级别
- 不为了快速通过编译把全部 Component、System 和辅助类公开
- 跨模块只读需求优先通过明确数据契约解决

每次提高可见性都需要确认调用方和长期所有权。

### 5.4 清理 Editor 反向依赖

Runtime、Authoring 和 Baker 不得依赖 Editor 程序集。

Navigation 试点前需要处理：

- 将纯 Editor 文件移动到明确的 Editor 编译边界，并保留 `.meta` GUID
- 把资产基础有效性检查与 Scene 当前状态检查分离
- Runtime 或 Authoring 只读取可在 Player 构建中存在的数据
- Scene 新鲜度、菜单、Inspector 和构建前门禁留在 Editor
- 验证 Baker 在 Editor Baking、Live Baking 和 Player Build 中都能被正确发现

在完成这项整理前，Navigation 第一轮保持单程序集，避免制造 Runtime 到 Editor 的非法引用。

### 5.5 消除循环依赖

发现循环依赖时按以下顺序处理：

1. 判断引用是否只是为了读取一个状态
2. 将共享状态移动到 Contracts
3. 将流程调用改为请求 Component、事件 Buffer 或明确接口
4. 调整 System 更新顺序而不是直接调用对方 System
5. 最后才考虑合并两个生命周期一致的模块

禁止通过反射、全局静态单例、`SendMessage` 或复制类型绕过程序集边界。

### 5.6 审计 Unity 序列化身份

移动脚本目录时必须保留 `.meta` GUID。把类型从 `Assembly-CSharp` 移入自定义程序集时，还会改变程序集限定类型名，因此需要额外检查：

- Scene 和 Prefab 中的 MonoBehaviour 是否出现 Missing Script
- ScriptableObject 资产能否正常加载
- `SerializeReference` 保存的托管类型是否需要 `MovedFrom`
- 自定义序列化数据是否保存了程序集限定名称
- SubScene 和 Entity Scene 是否需要重新烘焙
- Ghost Collection、NetCode Source Generator 和组件稳定 Hash 是否发生变化
- Build Profile 和预制体列表是否仍能解析类型

项目当前不承诺旧版本客户端与新程序集版本联网兼容，但同一个提交内的 Client 和 Server 必须重新生成并保持一致。

### 5.7 固定迁移基线

创建第一个 asmdef 前记录以下基线：

- 当前 Commit
- Unity、Entities、NetCode 和 Burst 版本
- Editor 编译结果
- Client 与 Server 构建结果
- 阶段一和阶段二 Navigation 验收结果
- Console 现有 Warning 清单
- 自有脚本数量和注释率
- 关键 Scene、Prefab、SO 和 SubScene 的可加载状态

后续每个阶段都与同一基线比较，避免把原有 Warning 误认为本次程序集迁移产生的回归。

## 6. 分阶段执行计划

### 阶段零：依赖审计与冻结

工作内容：

- 生成脚本归属清单
- 记录跨顶层目录引用
- 标记 Editor、Runtime、Authoring、Tests 和 Benchmark
- 找出初始循环依赖
- 暂停与程序集迁移无关的大范围目录调整

退出条件：

- 每个脚本都有预期程序集或明确的待定原因
- Navigation 试点没有未记录的项目业务依赖
- 当前工作区和基线 Commit 可重复构建

### 阶段一：Navigation 单程序集试点

创建：

```text
AnimarsCatcher.Navigation
```

初始设置建议：

- `Auto Referenced` 在迁移期间保持开启
- 项目程序集引用使用 GUID
- `Allow Unsafe Code` 默认关闭
- 不启用 `Override References`
- 只声明实际使用的 Unity Package 程序集
- 使用 `AnimarsCatcher.Navigation` 作为根命名空间提示

验证内容：

- `Assembly-CSharp` 可以继续消费 Navigation 公共 API
- Navigation 不能引用其他仍在 `Assembly-CSharp` 中的项目类型
- Editor 和 Player 编译都通过
- Stage One 和 Stage Two 自动验收通过
- Grid Scene、Bake Asset 和 Inspector 无 Missing Script
- Client、Server 和 Local World 能创建对应 Navigation System

退出条件：

- Navigation asmdef 在独立提交中稳定
- 没有通过扩大公开 API 或增加全局静态状态解决编译问题
- 回滚该提交可以完整恢复原编译结构

### 阶段二：提取 Core 与 Contracts

状态：已完成，实施范围和验证结果见 [程序集迁移实施与最终收紧](12_AssemblyMigrationPhaseSevenFinalTightening.md)。

工作内容：

- 从实际循环依赖中提取共享数据
- 为目标类型补充命名空间
- 缩小公共 API
- 建立 Core 和 Contracts 的单向依赖

退出条件：

- Core 不引用 Gameplay、Networking、Player 或 Presentation
- Contracts 不包含具体业务 System
- 依赖 Contracts 的模块不互相引用实现类型

### 阶段三：Gameplay 迁移

状态：已完成，实施范围和验证结果见 [程序集迁移实施与最终收紧](12_AssemblyMigrationPhaseSevenFinalTightening.md)。

工作内容：

- 迁移 Anis、Base、Camp、Health、Resource 和 Global
- 根据审计结果决定合并或保留领域程序集
- 修复 System 更新顺序和跨模块状态访问
- 重新烘焙相关 SubScene 和 Ghost Prefab

退出条件：

- 服务端玩法编译和运行通过
- 伤害、资源、阵营、基地和对局结果链路没有跨程序集循环
- Gameplay 不引用 UI 或 MonoBehaviour 实现

### 阶段四：Player 与 Networking 迁移

状态：已完成，实施范围和验证结果见 [程序集迁移实施与最终收紧](12_AssemblyMigrationPhaseSevenFinalTightening.md)。

工作内容：

- 先迁移双方共享协议
- 再迁移 Player 预测和输入
- 最后迁移连接、Spawn 和网络生命周期
- 检查 NetCode Source Generator 输出

退出条件：

- Client、Server 和 Thin Client World 创建正常
- 连接、进入 InGame、角色 Spawn 和预测移动通过
- Player 与 Networking 不双向引用具体 System

### 阶段五：Presentation 迁移

状态：已完成，实施范围和验证结果见 [程序集迁移实施与最终收紧](12_AssemblyMigrationPhaseSevenFinalTightening.md)。

工作内容：

- 迁移 ECS UI、Mono UI、音频、菜单和 GameObject View
- 用桥接数据或事件替代运行时模块对 UI 的反向调用
- 检查 Scene 中全部 MonoBehaviour

退出条件：

- 菜单、LAN、HUD、选择、血条、相机和结算界面正常
- Runtime 业务程序集不引用 Presentation

### 阶段六：Legacy Benchmark 隔离

状态：已完成，实施范围和验证结果见 [程序集迁移实施与最终收紧](12_AssemblyMigrationPhaseSevenFinalTightening.md)。

工作内容：

- 创建 Legacy Navigation Benchmark 程序集
- 显式引用其运行所需的正式契约
- 检查正式程序集依赖图中没有 Benchmark

退出条件：

- Legacy 场景仍可运行性能基线
- 删除或禁用 Benchmark 程序集不会影响正式玩法编译

### 阶段七：收紧依赖

状态：已完成，实施范围和当前验证结果见 [程序集迁移实施与最终收紧](12_AssemblyMigrationPhaseSevenFinalTightening.md)。

所有业务脚本离开 `Assembly-CSharp` 后：

- 关闭不必要的 `Auto Referenced`
- 移除过渡期 `.asmref`
- 删除未使用程序集引用
- 为 Runtime、Editor 和 Tests 建立最终边界
- 把程序集依赖图加入架构文档

实际结果：

- 剩余 Editor、Physics 和 Terrain 脚本已完成迁移
- Navigation 与 Networking 的 Runtime/Editor 混编已拆分
- 15 个项目 asmdef 已关闭 `Auto Referenced`
- 迁移期 asmref 已全部移除，当前数量为 0
- Presentation 未使用的 Transport 直接引用已删除
- 最终程序集依赖图和总验收入口已经加入项目

## 7. 每阶段验收标准

每次只迁移一个清晰边界，并至少完成以下检查：

1. Unity Editor 无新增编译 Error
2. Client、Server 和相关 Build Profile 编译通过
3. Console 无新增 Error
4. Scene、Prefab 和 ScriptableObject 无 Missing Script
5. SubScene、Baker 和 Ghost 数据完成必要重烘焙
6. 对应模块自动验收或关键手动链路通过
7. `.meta` 完整且 GUID 无重复
8. 没有新增 Runtime 到 Editor 的引用
9. 没有新增正式程序集到 Benchmark 的引用
10. 注释规范和手写文本检查通过
11. Git 提交只包含本阶段程序集迁移

Navigation 阶段额外要求：

- Stage One Grid 烘焙验收通过
- Stage Two 路径与异步 System 验收通过
- 固定 Grid Scene 和 Bake Asset 类型标识正确

## 8. 提交与回滚策略

程序集迁移必须拆成小提交：

1. 命名空间、目录和访问级别前置整理
2. 创建单个 asmdef 并修复引用
3. 修复序列化、Baker、SubScene 或 Ghost 数据
4. 增加测试程序集或依赖门禁
5. 更新架构文档

一个提交不同时迁移多个高层模块。不要把目录重构、玩法修改和程序集迁移混在一起。

每个阶段必须能够通过回滚该阶段提交恢复。禁止手工重新生成或替换原 `.meta` GUID 作为回滚手段。

推荐提交信息：

- `update: 整理 Navigation 程序集前置依赖`
- `update: 配置 Navigation 程序集边界`
- `fix: 修复程序集迁移后的序列化引用`
- `update: 拆分 Navigation Runtime 与 Editor 程序集`

## 9. 停止条件

出现以下情况时停止继续迁移，先解决当前阶段：

- 需要让 Runtime 引用 Editor 才能通过编译
- 出现两个业务程序集互相引用
- 需要把大量内部实现改为 `public`
- Scene、Prefab、SO 或 SubScene 出现无法解释的 Missing Script
- NetCode Source Generator 或 Ghost Collection 结果不稳定
- Client 和 Server 使用了不同的组件类型布局
- 自动验收无法重复通过
- 为解决边界问题引入新的全局静态单例

## 10. 实施顺序结论

项目不应现在一次性创建全部程序集。推荐顺序为：

```text
依赖审计
-> Navigation 单程序集试点
-> Core 与 Contracts
-> Gameplay
-> Player 与 Networking
-> Presentation
-> Legacy Benchmark
-> Runtime Editor Tests 最终拆分和依赖收紧
```

程序集迁移阶段零至阶段七已经完成实现。当前静态审计和 15 个实际项目 `.csproj` 编译通过；旧 Stage Seven Unity 总入口需先登记新增的 Navigation Benchmark 与 Validation 程序集，再补跑该入口、Client 和 Dedicated Server 构建。后续结构工作转向独立 Tests 程序集和持续构建门禁，不再以继续细拆业务程序集为目标。
