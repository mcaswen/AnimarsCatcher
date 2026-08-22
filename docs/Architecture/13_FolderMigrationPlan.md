# 文件夹迁移实施记录

[返回架构总览](README.md)

> 状态：资源迁移阶段一至阶段七与脚本层级收敛已完成
>
> 实施日期：2026-07-20
>
> 目标：让物理目录与程序集、资源职责和协作边界一致，同时保持资产 GUID、玩法行为和网络协议不变

## 1. 实施结果

本次迁移完成了脚本程序集归属、脚本多级目录、动画、Timeline、旧资源、场景、音频、Terrain 和生成缓存的集中整理。

资源迁移没有修改命名空间、Ghost 协议、序列化字段或核心玩法逻辑。脚本归属收敛将表现类型迁入 Presentation 命名空间，并通过 `MovedFrom` 保留序列化兼容；后续目录压平只移动脚本与 `.meta`，没有修改 C# 内容。

最终结果：

- 339 个 C# 文件全部处于明确 asmdef 覆盖范围
- 项目 asmdef 为 15 个，asmref 为 0
- 动画、Timeline、音频和 Terrain 资源均保留原 .meta GUID
- Assets/Resource 已删除
- 正式场景、SubScene、Benchmark 和 Legacy 场景已分区
- SceneDependencyCache 已删除并加入忽略规则
- Presentation 不再使用统一的 MonoBehaviour 技术目录，角色 View、选择、血条和攻击表现均由对应功能目录持有
- Gameplay/Resource 已按 Global、Player、Collection 和 Spawn 重组，Gameplay 与 Player 的无意义单文件职责目录已压平
- Unity 自动生成的解决方案文件不纳入迁移提交

## 2. 当前目录

### 2.1 脚本

~~~text
Assets/Scripts/
├── Core/
│   ├── Collections/
│   ├── Fsm/
│   └── Math/
├── Gameplay/
│   ├── Anis/
│   │   ├── Combat/
│   │   ├── FSM/
│   │   ├── Perception/
│   │   └── Spawn/
│   ├── Base/
│   ├── Camp/
│   ├── Contracts/
│   ├── Editor/
│   ├── Global/
│   ├── Health/
│   └── Resource/
│       ├── Collection/
│       ├── Global/
│       ├── Player/
│       └── Spawn/
├── Navigation/
│   ├── Grid/
│   │   ├── Static/
│   │   ├── Runtime/
│   │   ├── Overlay/
│   │   ├── Pathfinding/
│   │   ├── Hierarchical/
│   │   └── FlowField/
│   ├── Squad/
│   └── Tooling/
│       ├── Editor/
│       ├── Validation/
│       └── Benchmark/
├── Player/
├── Netcode/
├── Presentation/
│   ├── Account/
│   ├── Anis/
│   ├── Audio/
│   ├── Cameras/
│   ├── EntityView/
│   ├── HealthBars/
│   ├── InputLock/
│   ├── Lan/
│   ├── Match/
│   ├── Network/
│   ├── Player/
│   ├── Resource/
│   ├── Room/
│   ├── Selection/
│   ├── UI/
│   └── Vfx/
├── Physics/
│   └── Terrain/
├── Benchmarks/
└── Editor/
~~~

Tools/TransferCodeToTxt.ps1 已移到仓库根 Tools，不再由 Unity 导入。

### 2.2 资源

~~~text
Assets/
├── Art/
│   ├── Animations/
│   │   ├── Avatars/
│   │   ├── Clips/
│   │   └── Source/
│   ├── AnimationControllers/
│   ├── Environment/
│   │   ├── Terrain/
│   │   │   └── Brushes/
│   │   └── Vegetation/
│   │       ├── Materials/
│   │       ├── Models/
│   │       └── Textures/
│   └── Timelines/
├── Audio/
│   ├── BGM/
│   └── SFX/
│       ├── Ambience/
│       ├── Gameplay/
│       └── UI/
├── Prefabs/
│   ├── Local/
│   │   ├── Environment/
│   │   └── VFX/
│   ├── Network/
│   └── Legacy/
│       └── Resources/
├── Scenes/
│   ├── Benchmarks/
│   ├── Bootstrap/
│   ├── Gameplay/
│   ├── Legacy/
│   └── SubScenes/
├── Settings/
└── SO/
~~~

Plugins、Samples、StreamingAssets、TextMesh Pro 和 UPM 管理内容保持原位。

## 3. 阶段记录

### 3.1 Scripts 与程序集收敛

Navigation 从 Ani 目录中独立到 Scripts/Navigation/Grid。

Anis、Base、Camp、Global、Health 和 Resource 进入 Scripts/Gameplay。MonoBehaviour 与 UI 进入 Scripts/Presentation。Terrain Authoring 进入 Scripts/Physics/Terrain。

9 个 asmref 及对应 .meta 已删除。Tools/AssemblyMigrationRules.psd1 已切换到新路径，程序集审计结果为 272 个脚本全部覆盖、0 个边界错误、0 个警告。

提交：9680a29 update: 收敛脚本物理目录

### 3.2 Animation 与 Timeline

Animation Clip、Avatar Mask 和动画源文件进入 Art/Animations，Animator Controller 进入 Art/AnimationControllers，Timeline 进入 Art/Timelines。

全部文件由 Git 识别为 100% rename，没有资源内容改写。

提交：71d9d27 update: 迁移动画与Timeline资源

### 3.3 旧 Resource 清理

活动 Beam Prefab 进入 Prefabs/Local/VFX。

旧 Crystal 与 Fruit Prefab 进入 Prefabs/Legacy/Resources。两个与权威 Network Ani Prefab 内容完全相同的旧 Prefab 已删除，旧场景中的 GUID 已替换为权威 Prefab GUID。

DOTweenSettings.asset 进入 Assets/Settings，没有放入 Unity 特殊目录 Resources，因此不会把原本未启用的运行时自动加载行为意外打开。

提交：5660336 update: 清理旧Resource资源目录

### 3.4 Scene

当前启用场景路径为：

1. Assets/Scenes/Bootstrap/SCN_MainMenu.unity
2. Assets/Scenes/SubScenes/SCN_GameLevel_SubScene.unity
3. Assets/Scenes/Gameplay/SCN_GameLevel.unity

旧 SCN_Main、SCN_Start、SCN_MainTest 和 SCN_Level 进入 Scenes/Legacy。Grid 烘焙场景继续位于 Scenes/Benchmarks。

Build Settings 路径已更新，三个场景 GUID 保持不变。主场景对 SubScene 的引用本身使用 GUID，因此无需改写场景内容。

提交：9aa02d7 update: 整理场景目录与构建路径

### 3.5 Audio

5 个 BGM 进入 Audio/BGM。

环境音进入 Audio/SFX/Ambience，UI 音效进入 Audio/SFX/UI，其余音效进入 Audio/SFX/Gameplay。

17 个音频文件没有重新编码，Git 全部识别为 100% rename。

提交：12df4f8 update: 整理音频资源目录

### 3.6 Terrain 与 Environment

Terrain Data、Terrain Layer、纹理、物理材质和 Brush 进入 Art/Environment/Terrain。

植物模型、材质和纹理进入 Art/Environment/Vegetation。24 个树木与灌木 Prefab 进入 Prefabs/Local/Environment。

原目录中的 55 个资源全部迁移，61 个目录内 .meta 标识保持唯一，Git 全部识别为 100% rename。

提交：821bc0d update: 迁移Terrain与环境资源

### 3.7 缓存与规范

Assets/SceneDependencyCache 中的 sceneWithBuildSettings 文件已删除。

.gitignore 已加入 Assets/SceneDependencyCache，避免 Unity 再次生成缓存时污染工作区。

项目组织规范、模块地图、构建入口、Navigation 路径和存量整改记录已经同步到新目录。

### 3.8 脚本归属与多级目录收敛

攻击表现、选择射线桥接和角色 GameObject View 从 Gameplay 或 Player 迁入 Presentation。相关序列化类型保留原脚本 `.meta` GUID，并使用 `MovedFrom` 兼容旧命名空间。

Presentation 按 Account、Anis、Health、Match、Player、Selection、UI 等业务功能组织，移除了统一的 MonoBehaviour 目录和只承载单个脚本的 Authoring、Common、View 等层级。

Gameplay/Anis 保留 Combat、FSM、Perception 和 Spawn 等稳定边界；Gameplay/Resource 改为 Global、Player、Collection 和 Spawn。Player 保留输入、相机、角色控制和客户端生命周期目录，单文件职责层级直接压平到所属功能。

脚本层级收敛完成时的物理统计为 Gameplay 76 个脚本、33 个子目录，Player 42 个脚本、19 个子目录，Presentation 63 个脚本、24 个子目录。后续功能开发继续增加了文件；当前模块数量以架构总览和仓库扫描为准。

脚本目录压平阶段的 C# 内容改动为零，Git 将脚本与脚本 `.meta` 全部识别为 100% rename。删除内容仅为空目录对应的文件夹 `.meta`。

Unity Asset Pipeline 对主迁移批次记录为 120 项移动、44 项删除和 24 项变化，重新编译耗时约 5.9 秒；Selection 客户端补充批次记录为 8 项移动和 1 项删除，重新编译耗时约 2.7 秒。Editor.log 没有新增编译、导入、Missing Script 或重复 GUID 错误。

目录迁移结束后，阶段四至阶段六的临时总验收类已经删除。最终 Stage 7 入口直接调用当前 Gameplay 和 Navigation 模块验收，不再级联执行已经完成使命的阶段验证器。

提交：

- b84a302 update: 修正表现脚本程序集归属
- d0d0429 update: 按业务功能收敛Presentation目录
- 2426791 update: 收敛Gameplay与Player目录层级
- cf1aa64 update: 压平Selection客户端目录

## 4. 迁移中的约束

运行中的 Unity Editor 没有为外部创建的空目录自动生成 .meta。为避免手工生成 GUID，本次新增分类目录复用了已经确认可以移除的旧文件夹 .meta。

复用只发生在文件夹身份上，不涉及 Scene、Prefab、ScriptableObject、脚本或其他被序列化引用的资产 GUID。

场景伴生资源当前采用以下布局：

- Scenes/Gameplay/SCN_GameLevel 保留主场景 LightingData、Lightmap、ReflectionProbe 和旧 NavMesh 资产
- Scenes/SubScenes 保存 SubScene 和其旧 NavMesh 资产
- Scenes/Legacy 保存旧场景以及旧 SCN_Main 的光照和 NavMesh 资产

后续重新烘焙光照或导航数据时，应由 Unity 在对应场景目录重新生成，不手工复制旧数据。

## 5. 已完成门禁

已完成：

- 程序集审计通过，当前 339 个脚本全部覆盖，15 个 asmdef，0 个 asmref
- 迁移提交当时的注释规范检查通过，后续注释质量由当前门禁重新计算，不沿用历史比例
- Unity Asset Pipeline 已识别 395 项移动并完成脚本编译，Editor.log 没有新增编译或导入错误
- 全项目 .meta GUID 无重复
- Build Settings 中三个场景路径与 GUID 一致
- 运行时代码、工具和 ProjectSettings 中没有迁移前硬编码路径
- 音频、动画、Timeline、Terrain 和环境资源主要差异均为 100% rename
- Assets/Resource、Assets/Terrains 和旧脚本顶层目录已移除

仍需在 Unity 中进行交互式验收：

- 全部 Scene、Prefab 和 ScriptableObject 的 Missing Script 检查
- Animator、Timeline、Audio 和 Terrain 的编辑器内引用检查
- Grid 烘焙场景可视化检查
- Client 与 Dedicated Server 构建
- 主菜单进入游戏、SubScene 加载和第二局流程

这些运行时门禁不能用静态文本扫描代替。

## 6. 回滚方式

每个高风险目录边界均使用独立提交。出现问题时应整体回滚对应提交，不手工重新生成 .meta 或 GUID。

回滚顺序应与迁移顺序相反：

1. Terrain 与 Environment
2. Audio
3. Scene
4. Resource
5. Animation 与 Timeline
6. Scripts

场景引用替换与重复 Prefab 删除位于同一 Resource 提交，不能只恢复被删除 Prefab 而不恢复旧场景 GUID。
