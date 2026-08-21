# 项目组织与命名规范

[返回开发规范总目录](DevelopmentGuidelines.md)

## 1. 基本原则

1. 新增内容立即遵守本规范；存量内容只在专项迁移或修改所属模块时逐步整理。
2. 不为了目录或命名整齐进行无业务收益的大规模移动、重命名。
3. 项目资源、第三方资源、生成内容和源码必须保持清晰边界。
4. 同一资源只有一个权威位置，不保留“最终版”“最新版”等并行副本。
5. 临时内容必须放入明确的 `Dev`、`Debug`、`Prototype` 目录，并在正式合并前删除或归档。

## 2. 推荐目录

```text
Assets/
├── Art/
│   ├── Animations/
│   │   ├── Avatars/
│   │   ├── Clips/
│   │   └── Source/
│   ├── AnimationControllers/
│   ├── Environment/
│   │   ├── Terrain/
│   │   └── Vegetation/
│   ├── Materials/
│   ├── Models/
│   ├── Sprites/
│   ├── Textures/
│   ├── Timelines/
│   └── VFX/
├── Audio/
│   ├── BGM/
│   └── SFX/
│       ├── Ambience/
│       ├── Gameplay/
│       └── UI/
├── Prefabs/
│   ├── Local/
│   │   └── Environment/
│   ├── Network/
│   └── Legacy/
├── Scenes/
│   ├── Benchmarks/
│   ├── Bootstrap/
│   ├── Gameplay/
│   ├── SubScenes/
│   └── Legacy/
├── Scripts/
│   ├── Core/
│   ├── Gameplay/
│   │   ├── Anis/
│   │   ├── Base/
│   │   ├── Camp/
│   │   ├── Contracts/
│   │   ├── Editor/
│   │   ├── Global/
│   │   ├── Health/
│   │   └── Resource/
│   ├── Navigation/
│   │   └── Grid/
│   ├── Player/
│   ├── Netcode/
│   ├── Presentation/
│   │   ├── Account/
│   │   ├── Anis/
│   │   ├── Audio/
│   │   ├── Camera/
│   │   ├── Health/
│   │   ├── Match/
│   │   ├── Player/
│   │   ├── Selection/
│   │   └── UI/
│   ├── Physics/
│   │   └── Terrain/
│   ├── Benchmarks/
│   └── Editor/
├── SO/
├── Settings/
├── Shaders/
├── Plugins/
├── Samples/
├── StreamingAssets/
└── TextMesh Pro/

Packages/
ProjectSettings/
docs/
├── Standards/
└── Architecture/
```

脚本目录采用“程序集边界优先、业务功能优先、技术职责按需补充”的结构，不把全部 System、Component 或 MonoBehaviour 集中到全局技术目录。

```text
Scripts/<AssemblyRoot>/<Feature>/
├── <FeatureType>.cs
├── Components/          持续扩展的纯数据类型组
├── Systems/             持续扩展的运行系统组
│   ├── Client/          明确的客户端生命周期
│   └── Server/          明确的服务端生命周期
├── Editor/              Editor-only 程序集或生命周期
└── Utilities/           多个同类工具组成的稳定集合
```

具体规则如下：

1. `Assets/Scripts` 下第一层优先对应 asmdef 程序集边界，例如 `Gameplay`、`Player`、`Presentation` 和 `Navigation`。
2. 程序集内部先按业务功能划分，例如 `Gameplay/Resource/Player`、`Gameplay/Resource/Collection` 和 `Presentation/Selection`。
3. `Authoring`、`Components`、`Systems`、`Client`、`Server`、`Editor`、`Tests`、`Utilities` 只在确实形成稳定职责组时创建，不作为每个功能的固定模板。
4. 只有一个脚本且没有独立生命周期或扩展计划时，脚本直接放在所属功能目录，不额外套一层 `Authoring`、`Systems`、`Common` 或 `Utilities`。
5. `Client`、`Server`、`Editor`、`Tests` 等会改变运行位置、编译范围或验收方式的目录，即使文件较少也可以保留。
6. `Melee`、`Ranged`、`Algorithms`、`Runtime`、`Registry` 等能表达稳定业务分型或算法边界的目录可以保留。
7. 禁止使用 `Mono`、`MonoBehaviour` 作为跨业务分类目录。表现脚本应进入 `Presentation/<Feature>`，由功能归属表达职责。
8. 普通源码从 `Assets/Scripts` 到文件所在目录原则上不超过 5 层。超过时必须证明每一层都对应稳定业务、生命周期、编译或算法边界。
9. 不得为了形式完整创建空目录，也不得长期保留仅含空文件夹 `.meta` 的目录。
10. 物理目录用于帮助定位和控制程序集覆盖，不要求与命名空间逐段一致。命名空间仍按长期业务归属设计，不因压平技术目录而机械改名。

当前 Gameplay、Presentation、Physics 和 Navigation 的物理目录已经与 asmdef 覆盖范围对齐，项目 asmref 数量为 0。新增领域不得默认接入现有程序集，必须先通过依赖审计确认生命周期和允许依赖一致。

Player 使用 `AnimarsCatcher.Player` Runtime asmdef，Player Input Editor 使用独立的 `AnimarsCatcher.Player.Editor`。Netcode 使用 `AnimarsCatcher.Networking`。表现桥接脚本不得放回 Player 或 Netcode 来绕过程序集依赖，必须由 Presentation 程序集从上层引用运行时模块。

## 3. 存放要求

1. 正式场景统一放在 `Assets/Scenes`。
2. 业务 C# 源码统一放在 `Assets/Scripts`。
3. 使用 `UnityEditor` 的代码只能位于 `Editor` 目录、Editor-only asmdef 或正确的条件编译区域。
4. 测试脚本放在独立测试目录和测试 asmdef 中。
5. ScriptableObject 配置放在 `Assets/SO/<Domain>`，或模块明确约定的数据目录。
6. `Obsolete` 不是 Unity 编译排除目录。废弃源码应删除并依赖 Git 历史，或移至 Unity 项目外归档。经负责人确认、具有固定输入和指标的可执行性能基线不属于废弃源码，只能放在 `Assets/Scripts/Benchmarks`，并明确后端开关、启用范围、维护责任和删除条件。
7. 不新增 `Assets/Resource` 来代替 Unity 特殊目录 `Resources`。
8. 尽量避免 `Resources.Load`，优先使用 Inspector 引用、Authoring Registry 或 Prefab Registry。

## 4. 第三方内容

1. UPM 包保留在 `Packages`，不复制到 `Assets/ThirdParty`。
2. 已依赖固定厂商目录的插件不得为了目录整齐擅自移动。
3. 新导入的外部资源在确认不会破坏更新流程后，可放入 `ThirdParty/<Vendor>/<Package>`。
4. 第三方内容必须记录版本、来源、许可证和署名要求。
5. 不直接修改第三方源码；确需修改时应记录补丁，并优先通过包装层扩展。

## 5. C# 命名

1. 类型、方法、属性、枚举和常量使用 PascalCase。
2. 私有实例字段使用 `_camelCase`，局部变量和参数使用 `camelCase`。
3. Inspector 字段默认使用 `[SerializeField] private`。
4. MonoBehaviour、ScriptableObject 及主要公共类型的文件名必须与类型名一致。
5. 接口以 `I` 开头。
6. RPC 类型使用 `Rpc` 后缀，标识符使用 `Id`，例如 `StartMatchRequestRpc`、`NetworkId`。
7. `Manager` 仅用于确实管理生命周期或跨模块协调的类型。
8. DOTS 组件、RPC 和 Buffer Element 等纯数据结构允许使用 public 字段。
9. 私有静态字段与私有实例字段统一使用 `_camelCase`，不混用 `s_`、PascalCase 和 `_camelCase`。

### 5.1 缩写与变量生命周期

1. 同一概念在项目中只使用一种约定写法，不混用自创缩写。例如配置统一使用 `Config`，不同时出现 `Cfg`、`Conf`；标识符统一使用 `Id`，不混用 `ID`。
2. 十分常见且不会产生歧义的技术缩写可以直接使用，例如 `UI`、`Id`、`Rpc`、`Fsm`、`Config`、`Info`、`Min`、`Max`、`HP`、`HUD`、`IK`、`Lan`、`IP`、`VFX`、`SFX`、`BGM` 和 `AABB`。
3. 名称的可见范围越大、生命周期越长，越应使用完整单词；生命周期越短、上下文越明确，越允许使用约定俗成的缩写。
4. 类型名、文件名、公共 API、序列化字段、网络协议字段和长期状态只使用完整单词或项目统一缩写，不使用仅在当前作者上下文中才能理解的简写。
5. 私有字段、方法参数和跨越多个分支的局部变量应优先保持完整语义，例如 `_targetPosition`、`resourceConfig`、`requestEntity`。
6. 只在少量相邻语句中使用的局部变量可以采用常见缩写，例如 `targetPos`、`moveDir`、`velocity`、`deltaTime`；若离开当前语句块后含义不再明显，则改用完整名称。
7. 循环索引和明确的数学坐标允许使用 `i`、`j`、`k`、`x`、`y`、`z`、`t`，除此之外不使用单字母变量。
8. 禁止使用可能对应多个概念的单字母或自创缩写。例如不使用 `e` 表示 Entity、event 或 exception，应分别写成 `entity`、`eventData`、`exception`。
9. 常见词在不同作用域中的推荐写法如下：

| 概念 | PascalCase 标识符片段 | camelCase 片段或短局部 |
|---|---|---|
| Identifier | `Id` | `id` |
| Remote Procedure Call | `Rpc` | `rpc` |
| Finite State Machine | `Fsm` | `fsm` |
| User Interface | `UI` | `ui` |
| Hit Points | `HP` | `hp` |
| Heads-Up Display | `HUD` | `hud` |
| Inverse Kinematics | `IK` | `ik` |
| Local Area Network | `Lan` | `lan` |
| Internet Protocol | `IP` | `ip` |
| Axis-Aligned Bounding Box | `AABB` | `aabb` |
| Configuration | `Config` | `config` |
| Information | `Info` | `info` |
| Position | `Position` | `pos` 或 `position` |
| Direction | `Direction` | `dir` 或 `direction` |
| Velocity | `Velocity` | `vel` 或 `velocity` |
| Rotation | `Rotation` | `rot` 或 `rotation` |
| Squared | `Squared` | `sq` |
| Degrees | `Degrees` | `deg` |
| Command | `Command` | `cmd` 或 `command` |
| Request | `Request` | `req` 或 `request` |
| Response | `Response` | `resp` 或 `response` |
| Context | `Context` | `ctx` 或 `context` |
| Entity | `Entity` | `entity`，不使用 `e` |

10. 未列入统一表的缩写默认不进入公共命名；确需新增时先在 Code Review 中确认，并同步更新本表。
11. Unity、NetCode 和 NavMesh 等官方产品或 API 名称沿用官方大小写，不自行改写为 `Netcode`、`Net` 或 `Navmesh`。
12. 不为了统一缩写直接破坏序列化引用、Ghost 协议或公共 API；存量协议名称按迁移规范渐进处理。当前 `FsmVar`、`FsmVarType`、`FsmGraphRef` 及 Ghost 字段中的 `Deg` 属于兼容性保留名称，不作为新代码命名示例。

### 5.2 DOTS 类型命名

1. `IComponentData` 类型不统一添加 `Component` 后缀。组件身份由接口和使用位置表达，类型名应优先说明业务职责。
2. 不携带字段、只表达实体身份或状态存在性的组件使用 `Tag` 后缀，例如 `PlayerTag`、`AniSelectedTag`。
3. 持续变化的运行时数据使用 `State` 后缀；不使用 `Singleton` 表达业务含义，单例只是存储约束。
4. 烘焙或初始化后主要作为只读参数的数据使用 `Config` 后缀。
5. 只保存一个 Entity、Prefab 或托管对象引用的数据使用 `Reference` 后缀；保存一组同类引用时使用 `Registry` 后缀。
6. 一次性消费的命令数据使用 `Request` 后缀；结果通知使用 `Event` 或 `Notification` 后缀。
7. Buffer Element 使用单数业务名称。名称本身不能说明其为元素时使用 `Element`，事件缓冲使用 `Event`，引用缓冲使用 `Reference`。
8. 碰撞体尺寸、形状和局部几何数据使用 `Geometry` 或明确的形状名称，不使用含义宽泛的 `Info`。
9. DOTS 纯数据结构的 public 字段使用 PascalCase。MonoBehaviour 和 ScriptableObject 的 Inspector 字段仍默认使用 `[SerializeField] private`。
10. managed `IComponentData` 仅用于确实需要托管引用的数据。空 Tag 不得声明为 managed class。

### 5.3 端侧类型命名

1. 只在 Client World 运行的 System、发送器、连接入口和端侧桥接类型使用 `Client` 前缀。
2. 只在 Server World 运行的 System、接收器、权威处理器和端侧工具使用 `Server` 前缀。
3. 同时运行于 Client 和 Server、参与双方预测，或属于纯共享数据的类型不添加端侧前缀。
4. `Client`、`Server`、`Host` 目录只存放对应端侧职责。类型实际操作 Client World 时不得放入 `Server` 目录，反之亦然。
5. `Host` 表示同一进程内同时持有 Client 和 Server World 的产品角色。仅通过本地 Client World 发请求的 Host 入口放入 `Host` 目录，不视为 Server 入口。
6. 端侧前缀放在类型名开头，例如 `ClientWorldCommandRaycastSystem`、`ServerApplyDamageSystem`，不使用 `StartClientXxx`、`NetCodeClientXxx` 等位置不一致的写法。
7. 纯数据类型不因为暂时位于端侧目录就机械添加前缀；如果数据只被另一端消费，应先修正目录和所有权边界。

### 5.4 Authoring 与 Baker 命名

1. 仅用于把场景配置烘焙为 Entity 数据的 MonoBehaviour 使用 `Authoring` 后缀。
2. 默认把 Baker 声明为 Authoring 内部的 `private sealed class Baker : Baker<XxxAuthoring>`。
3. Baker 只有在逻辑复杂、需要独立测试或确实需要单独文件时才拆分；拆分后使用 `XxxBaker`，文件名必须与类型名一致。
4. Authoring 文件只保留 Authoring、本地专用 Baker 和紧密关联的小型数据类型。存在四个以上公共类型或跨运行时职责时，应拆分为 Components、Contracts 或独立类型文件。
5. 运行时确实承担注册、查询或生命周期维护职责的 MonoBehaviour 可以使用 `Registry`；只在 Bake 阶段提供 Prefab 的类型仍应使用 `Authoring`。
6. Authoring 和 MonoBehaviour 的序列化字段使用 `[SerializeField] private _camelCase`。迁移已有字段时使用 `FormerlySerializedAs` 保留 Scene、Prefab 和 SubScene 数据。

### 5.5 命名空间

1. 所有新增手写业务类型必须声明命名空间，禁止继续向全局命名空间增加类型。
2. 项目代码统一使用 `AnimarsCatcher` 根命名空间，第三方代码、Unity Sample 和生成代码除外。
3. 根命名空间之后优先使用稳定业务领域，不机械复制 `Assets/Scripts`、`Runtime`、`Algorithms`、`Components`、`Systems` 等物理目录。
4. 命名空间必须与类型的长期程序集归属一致。准备迁移到独立 asmdef 的类型，不使用另一个模块的命名空间临时过渡。
5. 同一程序集可以包含多个紧密相关的子命名空间，但一个文件只声明一个主要命名空间，不混放无关领域类型。
6. Editor 类型使用对应业务命名空间的 `.Editor` 子命名空间，Tests 使用 `.Tests`。命名空间后缀不能替代 Editor-only 或 Test asmdef 编译边界。
7. Client、Server 和 Shared 只有在类型职责确实不同且需要形成长期公共边界时才进入命名空间，不按当前 System Filter 机械拆分。
8. 命名空间片段使用 PascalCase 和完整业务单词。`UI`、`RPC` 等项目统一缩写可以保留，禁止使用个人缩写或单字母片段。
9. 跨程序集公共契约放在稳定领域或 `.Contracts` 命名空间，具体 System、Window、Controller 和调试工具不进入 Contracts。
10. 调整命名空间前必须检查 Scene、Prefab、ScriptableObject、`SerializeReference`、反射、字符串类型名、SubScene、Ghost 和 Source Generator；需要兼容旧序列化身份时使用明确迁移方案或 `MovedFrom`。
11. 命名空间迁移按模块独立提交，并保留脚本 `.meta` GUID。禁止同时批量改名、修改玩法行为和创建多个 asmdef。
12. 当前业务脚本的全局命名空间存量为 0，`Tools/GlobalNamespaceBaseline.txt` 只保留说明行。禁止向该基线登记新脚本；发现全局命名空间业务类型时必须在当前变更中归入正确命名空间。

推荐示例：

```text
AnimarsCatcher.Navigation.Grid
AnimarsCatcher.Gameplay
AnimarsCatcher.Gameplay.Contracts
AnimarsCatcher.Player
AnimarsCatcher.Networking
AnimarsCatcher.Presentation.Selection
AnimarsCatcher.Presentation.HealthBars
AnimarsCatcher.Benchmarks.LegacyNavigation
AnimarsCatcher.Player.Editor
```

不推荐示例：

```text
Assets.Scripts.Anis.Navigation.Grid.Systems
AnimarsCatcher.Runtime.Components
AnimarsCatcher.Temp
AnimarsCatcher.AC.Nav
```

## 6. 资源命名

| 类型 | 前缀 | 示例 |
|---|---|---|
| Prefab | `PFB_` | `PFB_Player_Robot` |
| ScriptableObject | `SO_` | `SO_Ani_BlasterAttributes` |
| Scene | `SCN_` | `SCN_MainMenu` |
| Material | `MAT_` | `MAT_Ani_BlasterBody` |
| Texture | `TEX_` | `TEX_Ani_Body_BaseColor` |
| Animation Clip | `ANIM_` | `ANIM_Blaster_Shoot` |
| Animator Controller | `AC_` | `AC_Blaster` |
| VFX | `VFX_` | `VFX_BlasterBeam` |
| Shader | `SH_` | `SH_ToonOutline` |
| BGM | `BGM_` | `BGM_MainMenu` |
| SFX | `SFX_` | `SFX_UI_Confirm` |
| Terrain Data | `TD_` | `TD_Terrain_01` |
| Terrain Layer | `TL_` | `TL_Terrain_Grass` |
| Terrain Brush | `BRUSH_` | `BRUSH_Terrain_Ground_01` |

不使用 `Png_`、`Fbx_` 等与文件扩展名重复的前缀。纹理用途使用 `_BaseColor`、`_Normal`、`_Mask`、`_Emission` 等后缀。

标准目录 `Tests`、`TestScenes` 和带明确对象名的测试内容是合法名称；禁止的是单独使用 `Test`、`New`、`Temp`、`Final`、`Latest` 等无有效语义名称。

## 7. 存量迁移

1. 每次迁移只处理一个模块。
2. 优先通过 Unity Editor 移动资产，并确保 `.meta` 与资产一起移动。
3. 场景、Prefab 或命名空间改名之前，必须搜索字符串加载、反射、动画事件、Ghost 配置和序列化引用。
4. 迁移后必须完成 Unity 重新导入、脚本编译、引用检查和目标平台构建。
5. 迁移提交不得混入功能开发或无关格式化。
