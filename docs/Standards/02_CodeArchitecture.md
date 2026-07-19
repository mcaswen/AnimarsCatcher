# 代码、DOTS 与 NetCode 规范

[返回开发规范总目录](DevelopmentGuidelines.md)

## 1. 代码职责与依赖

1. 一个类型应有一个主要职责，避免输入、规则计算、状态写入、UI、动画和音效全部集中在同一个类中。
2. 核心规则、网络同步、静态配置、运行时状态和表现逻辑应分离。
3. UI 回调只负责收集输入和调用业务入口，不在 `Button.onClick` 中堆积完整业务流程。
4. 模块之间优先通过明确接口、事件或领域服务通信，不直接修改对方内部可变状态。
5. 公共系统不得隐式依赖场景对象名称、Hierarchy 路径或未经声明的全局单例。
6. 优先复用项目已有实现，不创建职责重复的第二套事件总线、资源系统或生命周期管理器。

## 2. 方法与逐帧逻辑

1. 方法名必须明确表达行为，一个方法只处理一个主要阶段。
2. 循环中的 `return` 必须确认是退出整个方法，而不是本应跳过当前实体的 `continue`。
3. 早退、异常分支和失败路径必须与成功路径一样检查资源释放和状态回滚。
4. 禁止空 `catch`；确需忽略异常时必须说明原因并提供安全回退。
5. `Update`、`LateUpdate`、`FixedUpdate` 和 System `OnUpdate` 只保留必须逐帧执行的逻辑。
6. 不在热路径中重复执行：
   - `GameObject.Find`、`FindObjectOfType` 或全场景扫描。
   - 可提前缓存的 `GetComponent`。
   - 临时创建并遗忘的 `EntityQuery`。
   - 无必要的 LINQ、字符串拼接和托管分配。
   - 每实体、每帧重新计算完整 NavMesh 路径。
   - 高频 `Debug.Log`。
7. 事件驱动可以解决的问题不使用持续轮询。
8. 逐帧查询必须评估数据规模和复杂度，避免未经评估的 O(N²) 逻辑。

## 3. 序列化与访问控制

1. 默认使用 `private`。
2. Inspector 引用使用 `[SerializeField] private`。
3. 公共状态通过只读属性、方法或接口公开。
4. 修改序列化字段名时使用 `FormerlySerializedAs`，或提供明确迁移方案。
5. 不在运行时修改 ScriptableObject 原始资产来保存对局状态。
6. 不手工修改 `.meta` GUID。
7. `UnityEditor` API 只能存在于 Editor-only 编译范围，Runtime 类型不得依赖 Editor 类型。

### 3.1 Inspector 字段提示

1. `Tooltip` 只用于补充字段名、字段类型、`Header`、`Range` 或枚举选项无法直接表达的信息。
2. 只把英文字段名翻译成中文，或原样重复字段名和枚举选项时，省略 `Tooltip`。
3. 单位、有效范围、阈值触发后的行为、回退策略和非直观副作用适合使用 `Tooltip` 说明。
4. 保留的提示使用简短中文；API、类型名和通用缩写可以保留英文，末尾不使用中文句号或英文句号。
5. 可由 `Range`、`Min`、枚举或自定义 Inspector 明确表达的约束，优先使用对应编辑器能力，不用长段提示替代。

```csharp
[SerializeField] private float _connectTimeoutSeconds = 5f;

[Tooltip("超时后尝试备用 IP，单位秒")]
[SerializeField] private float _discoveryTimeoutSeconds = 5f;
```

## 4. 注释与编码

### 4.1 注释质量

1. 注释用于解释设计原因、约束、协议、生命周期和非直观行为，不逐句翻译代码。
2. 公共类型、公共 API 和受保护 API 使用 XML 文档注释；网络协议、状态机等公开契约必须说明用途和约束。
3. 公共类中的私有成员和默认私有成员不使用 XML 文档注释；自解释实现不写注释，确需解释原因、约束或非直观行为时使用中文 `//` 注释。
4. 删除 AI 生成残留的“第一步、第二步、新增代码、引用命名空间”等无上下文价值注释。
5. 临时调试日志、注释掉的大段旧实现和无效 `using` 不进入正式提交。
6. `#pragma warning disable` 必须限定范围并说明原因。
7. 注释正文统一使用中文；API、类型名、参数名和业内通用缩写可以保留英文，但完整语义必须由中文表达。
8. 注释正文结尾不使用中文句号 `。` 或英文句号 `.`；XML 标签独占一行时不受此限制。
9. 公共 API 的说明即使只有一行，`<summary>`、正文和 `</summary>` 也必须各占一行，禁止写成 `/// <summary>说明</summary>`。
10. 寻路、空间划分、几何计算、数值计算、状态同步等复杂算法必须提供阶段级注释，帮助维护者理解算法模型和正确性依据。
11. 复杂算法注释按实际风险说明输入数据的语义、选择该算法的原因、关键不变量或公式、边界条件、失败路径、保守性取舍与时间或空间复杂度，不要求机械覆盖每一项。
12. 注释应放在算法阶段、非直观公式和关键分支之前，解释“为什么成立”和“修改时不能破坏什么”，不逐行复述循环、赋值和条件判断。
13. 算法参考论文、文章或开源实现时，应记录可长期访问的来源，并说明项目内的关键改动和适用边界；禁止复制大段原文充当注释。
14. 修改算法模型、数据含义或边界策略时，必须同步更新相关注释、测试和架构文档，过时注释按缺陷处理。
15. `Baker.Bake`、`ISystem.OnCreate`、`ISystem.OnUpdate`、`ISystem.OnDestroy`、MonoBehaviour 生命周期函数、Editor 回调以及简单的接口实现或重写方法属于框架模板入口；当方法职责和实现都直接明确时，不要求添加 XML 文档或普通注释，也不为重复说明模板职责而补注释。
16. 模板入口包含复杂生命周期、System 排序、World 边界、资源所有权、异步状态、失败回滚或其他非直观副作用时，只在相关阶段和关键分支前使用中文注释解释约束；方法级说明仅在入口本身形成公共契约且无法从类型和签名直接理解时添加。

正确格式：

```csharp
/// <summary>
/// 获取当前玩家资源
/// </summary>
public PlayerResourceState GetPlayerResource()
```

### 4.2 注释率

1. 项目自有业务 C# 源码的注释率必须大于等于 **15%**。
2. 推荐将模块注释率维持在 **17% 左右**，合理区间为 16% 至 18%。
3. 注释率按模块或本次提交涉及的业务源码整体统计，不要求每个数据组件和每个短文件单独达到 15%。
4. 默认计算方式：

```text
注释率 = 注释行数 /（注释行数 + 有效代码行数）× 100%
```

5. 注释行包括 `//`、XML 文档注释和有效块注释；空行不计入分子或分母。
6. 自动生成代码、第三方代码、Unity Samples、代码转储和明确的迁移归档不参与统计。
7. 禁止通过重复描述代码、堆积分隔线、保留废弃代码或生成无意义注释提高比例。
8. 注释率是最低质量门槛，不替代 Code Review。即使达到比例，无效或过时注释仍必须修改。
9. 数据结构可能低于推荐值，协议、状态机、复杂系统和生命周期代码应高于推荐值，以整体达到目标。
10. 提交前运行 `powershell -ExecutionPolicy Bypass -File Tools/CheckCommentStyle.ps1`，检查注释率、中文注释、句号结尾、Inspector 提示、顶层公共类型 XML 文档、模板回调 XML 文档和被注释掉的旧代码。

### 4.3 文件格式

1. 源文件统一使用 UTF-8。
2. 行尾由仓库 `.gitattributes` 控制，不因单次功能修改批量转换无关文件。
3. 提交前检查新增和修改文本是否出现乱码；存量乱码使用独立提交处理。
4. 本节格式要求只约束手写源码和手写文档；Unity 序列化文本、生成代码、二进制和十六进制数据按 Git 规范的非手写文件例外处理。

## 5. Assembly Definition

1. 新增规模较大的稳定模块时，建议使用 asmdef 表达依赖边界，但不为每个小目录机械创建程序集。
2. 稳定模块的最终结构应为 Runtime、Editor 和 Tests 不同程序集边界；迁移试点确需暂时混编时，必须登记原因、使用正确条件编译并通过 Editor 与 Player 构建验证。
3. Runtime asmdef 禁止引用 Editor asmdef，也禁止出现未被正确条件编译隔离的 `UnityEditor` API。
4. asmdef 依赖必须单向，禁止为了临时调用形成循环引用。
5. Client、Server 和 Shared 程序集归属应与 World 职责一致；共享协议不得依赖 UI 或表现层。
6. 现有 `Assembly-CSharp` 内容按模块渐进迁移，不要求一次拆分全部脚本。
7. 调整 asmdef 后必须验证 Baker、Source Generator、Ghost CodeGen、Editor 工具和测试程序集。
8. 创建或调整项目 asmdef 前，更新 `Tools/AssemblyMigrationRules.psd1` 并运行 `Tools/AuditAssemblyMigration.ps1`，确认脚本归属和候选双向依赖。
9. 类型从 `Assembly-CSharp` 迁入自定义程序集时，必须检查 Scene、Prefab、ScriptableObject 和其他程序集限定类型名，并为固定资源增加可重复的序列化回归检查。
10. Core 不保存玩法语义、具体 System、场景引用或 UI 类型；Gameplay Contracts 只保存跨模块共享数据，不保存流程控制和静态运行状态。
11. 对标记为稳定边界的程序集必须在迁移规则中声明允许依赖，审计出现未允许的项目依赖时不得提交。
12. 多个物理目录确实属于同一生命周期和同一依赖边界时，可以通过 asmref 编译到同一程序集；禁止用 asmref 隐藏循环依赖、跨越 Runtime 与 Editor 职责或绕过模块所有权。
13. `AnimarsCatcher.Gameplay` 只依赖 Core、Gameplay Contracts 和必要 Unity Package；Player、Networking、Presentation 与 Legacy 只能从上层消费 Gameplay，不得被 Gameplay 反向引用。
14. `AnimarsCatcher.Player` 只依赖 Gameplay 和必要 Unity Package，不依赖 Networking、Presentation 或 Legacy；UI 输入锁和过场控制由表现层从上层桥接。
15. `AnimarsCatcher.Networking` 可以依赖 Gameplay Contracts、Gameplay、Player 和必要 Unity Package，不依赖 Presentation；网络生命周期变化通过数据通知或事件发布，由 Presentation 决定具体界面行为。
16. `AnimarsCatcher.Presentation` 统一承载 Mono UI、ECS UI、音频、LAN、HUD、场景过渡和 GameObject View，可以依赖 Gameplay Contracts、Gameplay、Player 与 Networking；运行时业务程序集不得反向引用 Presentation。
17. `AnimarsCatcher.Benchmarks.LegacyNavigation` 可以依赖正式运行时程序集以执行历史基线，但 Core、Gameplay、Navigation、Player、Networking 和 Presentation 不得引用 Benchmark；Benchmark 修复仅限可编译性、正确性和测量噪声，不继续承载正式功能。

## 6. Entities 与 DOTS

### 6.1 World 边界

1. 每个系统必须明确运行于 Server、Client、Thin Client、Editor 或 Local World。
2. 使用 `WorldSystemFilter`、系统组和 `RequireForUpdate` 表达运行边界。
3. 不依赖“找到的第一个 World”作为长期设计，需要目标 World 时必须明确筛选。
4. 不在多个 World 之间无保护地共享静态可变状态。
5. 销毁 Client/Server World 前必须有明确的退出、重建或应用结束方案。
6. `EntityManager` 是值类型，不与 `null` 比较；跨帧桥接应保存并检查 `World.IsCreated`。

### 6.2 System 顺序与查询

1. `OnCreate` 中声明必要的 `RequireForUpdate`。
2. 有先后依赖的系统使用 `UpdateBefore`、`UpdateAfter` 和系统组明确排序。
3. 不依赖默认排序保证伤害、死亡、结算和清理顺序。
4. 结构变化优先使用 EntityCommandBuffer，并明确回放时机。
5. 高频查询应缓存 EntityQuery、Lookup 和 TypeHandle；Lookup 和 TypeHandle 在使用前按要求更新。
6. 主线程托管 API 与 Burst 路径必须分开。需要调用 `UnityEngine.AI` 等托管 API 时，应取消不成立的 Burst 标记或隔离处理阶段。
7. System 字段只属于当前 World；跨 World 状态必须显式设计。

### 6.3 NativeContainer 生命周期

1. `Allocator.Temp` 只在当前执行范围内使用。
2. `Allocator.TempJob` 必须在允许生命周期内释放。
3. `Allocator.Persistent` 必须由明确所有者在 `OnDestroy` 或 Dispose 中释放。
4. 每条早退路径都必须检查 NativeContainer、EntityCommandBuffer 和 Query 是否需要释放。
5. 静态 NativeContainer 默认禁止；确需使用时必须具备多 World 引用计数、重复初始化和重复销毁保护。
6. Dispose 后必须重置初始化状态，避免后续 World 重建访问已释放内存。

### 6.4 Component、Buffer 与 Singleton

1. IComponentData 只保存必要、可序列化和可同步的状态。
2. 动态列表使用 DynamicBuffer，不在组件中隐藏托管集合。
3. Tag 和 Enableable Component 的语义必须清晰，不使用多个重叠 Tag 表达同一状态。
4. Singleton 只能有一个创建责任方，不得同时由 Baker 和 Runtime System 创建。
5. 调用 `GetSingleton` 前必须通过设计保证唯一性；不确定时使用查询或 `TryGetSingleton` 并处理异常状态。
6. Buffer 的 Add、Set、Append 语义必须与数据保留需求一致，避免覆盖其他系统同帧写入。

### 6.5 Authoring 与表现

1. Authoring 和 Baker 只负责从编辑器数据生成 Entity 数据，不承担运行时规则。
2. Runtime Entity、GameObject View 和配置资产必须有明确绑定关系。
3. 表现桥接可以消费权威状态并触发动画、音效和 VFX，但不能决定最终伤害、资源和胜负。
4. 绑定 Entity 的 View 必须处理 Entity 销毁、World 销毁和场景卸载。
5. 动画事件只作为表现时间点或请求，不作为未经服务器校验的权威命中结果。

## 7. NetCode

### 7.1 服务器权威

以下状态必须由服务器决定：

- 玩家资源和消费结果。
- 单位生成数量和生成资格。
- 伤害、命中、死亡和胜负。
- 阵营、所有权和 CommandTarget。
- 对局开始、结束及允许加载的场景。
- 可交互目标和交互结果。

客户端只发送输入、意图或受限请求，不发送可以直接应用的最终结果。

### 7.2 RPC 校验

服务器处理客户端 RPC 时必须检查：

1. `SourceConnection` 是否有效并处于允许状态。
2. 请求者是否拥有或有权控制目标实体。
3. 数量、索引、字符串、坐标和枚举是否在合法范围内。
4. 当前游戏阶段是否允许请求。
5. 请求频率、冷却和序号是否合法。
6. 目标实体是否存在、存活且组件完整。
7. 资源是否足够，事务是否能原子完成。
8. 命中距离、视线、阵营和服务端记录目标是否一致。

禁止客户端提供任意场景名并直接驱动所有客户端加载，应使用服务器维护的场景 ID、枚举或白名单配置。

### 7.3 输入、预测与结算

1. 玩家移动使用固定 Tick 的 ICommandData 或项目统一命令结构。
2. 客户端预测只预测允许预测的状态，服务器结果为最终结果。
3. 表现帧、固定物理 Tick 和网络 Tick 不混为同一时间来源。
4. 预测系统必须可重放、尽量确定性，避免在重复预测 Tick 中产生无法回滚的音效、生成、资源扣除或其他非幂等副作用。
5. 资源消费与单位生成必须作为一个服务器事务执行，不能由 UI 分别发送“扣资源”和“生成单位”。
6. 远程攻击的射线、目标和伤害应由服务器计算或完整复核。

### 7.4 连接与会话

1. Host、Client 和 Dedicated Server 启动参数使用统一定义。
2. 建房、加入、取消、断线、返回菜单和第二局必须有完整回收路径。
3. UI 返回按钮不能只隐藏面板；连接、监听和 LAN 广播必须按设计停止或复用。
4. Dispose World 后再次进入联机流程时必须显式重建所需 World。
5. 断线和超时后清理连接请求，允许用户安全重试。
6. Dedicated Server 不得依赖 Client World 触发服务器场景或 SubScene 初始化。

### 7.5 Ghost

1. 只同步客户端确实需要的字段。
2. GhostOwner、预测模式和发送目标必须在 Prefab 上明确配置。
3. 不通过 Entity 名称字符串判断关键 Prefab 是否就绪，应使用显式 Tag 或 Registry。
4. Ghost、Command、RPC 字段或 Variant 变更属于网络协议变更，必须 Review，并验证 Host、Client 和 Server World。
