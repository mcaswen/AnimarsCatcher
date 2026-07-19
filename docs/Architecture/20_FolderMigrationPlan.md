# 文件夹迁移计划

[返回架构总览](README.md)

> 状态：迁移前审计完成，尚未移动资产
>
> 目标：让物理目录与程序集、资源职责和协作边界一致
>
> 前置条件：先提交当前程序集迁移改动，并恢复 Unity 总验收能力

## 1. 迁移原则

文件夹迁移会改变大量资产路径，但不应改变资产 GUID、玩法行为或网络协议。

执行时遵循以下原则：

1. 每个资产与对应 `.meta` 必须一起移动，GUID 保持不变
2. 优先使用 Unity `AssetDatabase.MoveAsset`，不在 Unity 运行时直接拖拽大批文件
3. 一次提交只迁移一个明确目录边界
4. 文件夹移动与文件重命名分开执行
5. 不在同一提交修改玩法、序列化字段、Ghost 协议或场景业务内容
6. 每阶段都保留迁移清单、GUID 快照和可重复验收入口
7. 发现字符串路径、第三方加载约定或生成目录时先停止对应项，不用猜测替换

当前工作区仍包含程序集迁移和文档整合改动。开始第一次实际移动前，应先形成独立提交，使文件夹迁移可以按阶段回滚。

## 2. 当前目录审计

### 2.1 Assets 顶层

当前需要评估的项目目录包括：

- `Assets/Animations`：44 个文件，约 13.48 MB
- `Assets/Resource`：24 个文件，约 0.85 MB
- `Assets/Terrains`：116 个文件，约 58.45 MB
- `Assets/Timeline`：2 个文件
- `Assets/Audio`：34 个文件，约 241.14 MB
- `Assets/Scenes`：47 个文件，约 111.12 MB
- `Assets/Scripts`：272 个 C# 脚本，物理目录仍保留迁移前布局

以下目录不能因为顶层不整齐直接移动：

- `Assets/Plugins`：Unity 特殊目录和现有第三方插件位置
- `Assets/Samples`：UPM Sample 内容
- `Assets/TextMesh Pro`：厂商固定目录
- `Assets/StreamingAssets`：Unity 特殊运行时目录
- `Assets/Settings`：项目和渲染配置，移动前必须逐项确认加载方式

`Assets/SceneDependencyCache` 包含生成的 `sceneWithBuildSettings` 缓存。它不应迁入正式资源目录，后续需要确认生成方，再决定删除并加入忽略规则。

### 2.2 Scripts 物理布局

程序集迁移已经完成，但物理目录仍保留旧边界：

- Anis、Base、Camp、Global、Health 和 Resource 位于 `Scripts` 顶层，通过 6 个 asmref 汇入 Gameplay
- MonoBehaviour 和 UI 位于 `Scripts` 顶层，通过 2 个 asmref 汇入 Presentation
- Terrain 与 Physics 分处两个顶层目录，通过 1 个 asmref 汇入 Physics Authoring
- `Scripts/Presentation` 目前只有中心 asmdef
- `Scripts/Tools` 只有一个 PowerShell 文件，不属于 Unity C# 源码

这 9 个 asmref 在程序集迁移阶段是明确且安全的物理聚合方式。文件夹迁移完成后，如果相关源码移动到 asmdef 覆盖目录内，就可以删除这些 asmref。

### 2.3 Resource 目录

`Assets/Resource` 不是 Unity 特殊目录 `Resources`，不能把它视为运行时资源加载目录。

当前内容分为四类：

- `PFB_Ani_Blaster` 与 `PFB_Ani_Picker` 和 `Assets/Prefabs/Network/Anis/Mono` 中的 Prefab 内容完全相同，但 GUID 不同
- 两个重复 Ani Prefab 只被 `SCN_Main`、`SCN_Start`、`SCN_MainTest` 和 `SCN_Level` 等旧场景引用
- `PFB_VFX_Beam` 被当前 `PFB_Ani_Blaster_View` 引用，属于活动 Local VFX
- Crystal 与 Fruit Prefab 主要被旧场景引用，部分资产当前没有序列化引用

`DOTweenSettings.asset` 当前没有项目资产引用，但 DOTween DLL 包含 `Resources` 和 `DOTweenSettings` 加载逻辑。它不能在没有运行时验证的情况下随意移动到 `Assets/Resources`，否则可能从“未加载配置”变成“加载配置”，产生行为变化。

### 2.4 高引用资产

动画、地形和 Timeline 都主要通过 GUID 被 Scene、Prefab、Controller 或其他资产引用，移动本身通常不会断开引用，但必须经过 Unity 重新导入验证。

- `Assets/Animations` 有 24 个 `.meta` 标识，当前统计到约 30 处序列化引用
- `Assets/Terrains` 有 61 个 `.meta` 标识，当前统计到约 113 处序列化引用
- `Assets/Timeline` 有 1 个资源标识和 1 处序列化引用

Terrain 是当前风险最高的资源目录，应最后单独迁移。

### 2.5 路径字符串热点

当前已确认的硬编码路径包括：

- `AssemblyMigrationStageSixValidation` 中的 Legacy Prefab 路径
- Navigation 验收中的固定 Scene 与 Bake Asset 路径
- `EditorBuildSettings.asset` 中三个启用场景的文本路径
- Gameplay 验收对 `Assets/Scenes` 和 `Assets/Prefabs` 根目录的扫描
- `RampTextureCreator` 写入 `Application.dataPath + "/RampTexture.png"`

通过 GUID 引用的普通 Scene 和 Prefab 字段不需要手工改 YAML。字符串路径、构建场景路径、反射类型名和第三方固定路径必须显式更新。

## 3. 推荐目标结构

脚本目录按程序集和稳定职责组织：

```text
Assets/Scripts/
├── Core/
├── Gameplay/
│   ├── Anis/
│   ├── Base/
│   ├── Camp/
│   ├── Global/
│   ├── Health/
│   ├── Resource/
│   ├── Contracts/
│   └── Editor/
├── Navigation/
│   └── Grid/
├── Player/
├── Netcode/
├── Presentation/
│   ├── MonoBehaviour/
│   └── UI/
├── Physics/
│   └── Terrain/
├── Benchmarks/
├── Editor/
└── Tools/
```

关键决策：

- Navigation 使用独立顶层目录，因为它已经是独立程序集，不继续嵌在 Gameplay Anis 目录中
- Anis 其余玩法代码进入 Gameplay，但保留 Anis 领域子目录
- MonoBehaviour 与 UI 进入 Presentation，保持现有子目录结构，不在本轮重新按功能打散
- Terrain Authoring 进入 Physics 目录
- `TransferCodeToTxt.ps1` 移到仓库根 `Tools`，不继续作为 Unity Asset 导入

资源目录建议调整为：

```text
Assets/
├── Art/
│   ├── Animations/
│   │   ├── Clips/
│   │   ├── Avatars/
│   │   └── Source/
│   ├── AnimationControllers/
│   ├── Environment/
│   │   └── Terrain/
│   └── Timelines/
├── Audio/
│   ├── BGM/
│   ├── SFX/
│   └── Mixers/
├── Prefabs/
│   ├── Local/
│   ├── Network/
│   └── Legacy/
├── Scenes/
│   ├── Bootstrap/
│   ├── Gameplay/
│   ├── SubScenes/
│   ├── Benchmarks/
│   └── Legacy/
├── SO/
├── Settings/
└── Shaders/
```

Terrain 的树木 Prefab 最终应进入 `Prefabs/Local/Environment`，模型、材质和纹理进入 `Art/Environment/Vegetation`。第一轮 Terrain 迁移可以先整体进入 `Art/Environment/Terrain`，确认引用稳定后再做第二次类型拆分，避免一次移动 61 个 GUID 到多个目标目录。

## 4. 路径迁移映射

### 4.1 脚本

```text
Assets/Scripts/Anis/Navigation/Grid
-> Assets/Scripts/Navigation/Grid

Assets/Scripts/Anis
-> Assets/Scripts/Gameplay/Anis

Assets/Scripts/Base
-> Assets/Scripts/Gameplay/Base

Assets/Scripts/Camp
-> Assets/Scripts/Gameplay/Camp

Assets/Scripts/Global
-> Assets/Scripts/Gameplay/Global

Assets/Scripts/Health
-> Assets/Scripts/Gameplay/Health

Assets/Scripts/Resource
-> Assets/Scripts/Gameplay/Resource

Assets/Scripts/MonoBehaviour
-> Assets/Scripts/Presentation/MonoBehaviour

Assets/Scripts/UI
-> Assets/Scripts/Presentation/UI

Assets/Scripts/Terrain
-> Assets/Scripts/Physics/Terrain

Assets/Scripts/Tools/TransferCodeToTxt.ps1
-> Tools/TransferCodeToTxt.ps1
```

迁移后删除相应 asmref，并更新：

- `Tools/AssemblyMigrationRules.psd1`
- `Tools/AuditAssemblyMigration.ps1` 的预期路径与 asmref 数量
- 架构和开发规范中的物理目录描述
- 自动验收中存在的硬编码脚本或资源路径

命名空间在本轮保持不变。文件夹迁移不同时执行 namespace 重命名。

### 4.2 动画与 Timeline

```text
Assets/Animations/Clips
-> Assets/Art/Animations/Clips

Assets/Animations/Avatars
-> Assets/Art/Animations/Avatars

Assets/Animations/Fbx
-> Assets/Art/Animations/Source

Assets/Animations/Controllers
-> Assets/Art/AnimationControllers

Assets/Timeline
-> Assets/Art/Timelines
```

### 4.3 旧 Resource

推荐处理方式：

1. 把活动的 `PFB_VFX_Beam` 移到 `Assets/Prefabs/Local/VFX`
2. 让旧场景改用 `Assets/Prefabs/Network/Anis/Mono` 中的权威 Ani Prefab
3. 删除两个内容完全重复、GUID 不同的 Ani Prefab
4. 旧 Crystal 与 Fruit Prefab 连同旧场景进入 `Assets/Prefabs/Legacy/Resources`
5. 没有引用的旧 Prefab 由负责人确认后删除，不默认迁入正式目录
6. `DOTweenSettings.asset` 单独验证后决定保留、移动到插件要求的位置或重新生成

这一步包含引用替换和旧资产判定，不与动画或 Terrain 迁移混在同一提交。

### 4.4 场景

```text
Assets/Scenes/SCN_MainMenu.unity
-> Assets/Scenes/Bootstrap/SCN_MainMenu.unity

Assets/Scenes/LevelScene/SCN_GameLevel.unity
-> Assets/Scenes/Gameplay/SCN_GameLevel.unity

Assets/Scenes/LevelScene/SCN_GameLevel_SubScene.unity
-> Assets/Scenes/SubScenes/SCN_GameLevel_SubScene.unity

SCN_Main、SCN_Start、SCN_MainTest、SCN_Level
-> Assets/Scenes/Legacy/
```

场景旁的 LightingData、Lightmap、ReflectionProbe 和 NavMesh 目录必须与所属场景一起规划。`EditorBuildSettings.asset` 的路径、SubScene 引用和自动验收路径必须同步验证。

### 4.5 音频

当前 17 个音频文件已经使用 `BGM_` 和 `SFX_` 前缀，可以只按类型移动：

```text
BGM_*
-> Assets/Audio/BGM/

SFX_Ambience_*
-> Assets/Audio/SFX/Ambience/

SFX_UI_*
-> Assets/Audio/SFX/UI/

其他 SFX_*
-> Assets/Audio/SFX/Gameplay/
```

音频约 241 MB，移动后应检查 Git 状态是否只表现为 rename，避免因工具重写二进制造成仓库体积翻倍。

## 5. 不迁移与单独处理的目录

本轮保持原位：

- `Assets/Plugins`
- `Assets/Samples`
- `Assets/TextMesh Pro`
- `Assets/StreamingAssets`
- UPM 管理的 `Packages`

单独处理：

- `Assets/SceneDependencyCache`：确认生成方后删除并加入忽略规则
- `Assets/Settings`：只在具体配置归属明确时移动
- 第三方 Shader：保持厂商路径或独立登记，不并入项目自有 Shader 目录
- DOTween Settings：先验证插件加载约定

## 6. 实施阶段

### 阶段零：冻结基线

1. 提交当前程序集迁移和文档整合
2. 记录全部待迁移资产 GUID 与路径
3. 确认主分支没有其他人同时编辑目标 Scene、Prefab 或 Animator Controller
4. 恢复 Unity Licensing 后补跑程序集阶段七总验收
5. 建立文件夹迁移专用 Unity Editor 执行器，支持 dry-run 和 `AssetDatabase.MoveAsset`

退出条件：工作区干净，程序集审计、注释检查和 Unity 编译基线明确。

### 阶段一：Scripts 与 asmref 收敛

按以下提交拆分：

1. Navigation 物理目录迁移
2. Gameplay 六个领域目录迁移
3. Presentation 目录迁移
4. Physics Terrain 与仓库 Tools 迁移

每次只移动文件和更新路径规则，不修改代码职责或命名空间。

退出条件：

- 272 个脚本仍全部被程序集规则覆盖
- 项目 asmref 从 9 个降到 0
- 13 个项目 asmdef 名称和 GUID 不变
- Unity Editor、Entities、NetCode 和程序集总验收通过
- Scene、Prefab 和 ScriptableObject 没有 Missing Script

### 阶段二：Animations 与 Timeline

先移动 Animation Clip、Avatar Mask、动画源文件、Controller 和 Timeline。

退出条件：

- Animator Controller 的 State 与 Clip 引用完整
- Ani、Player 和 Cutscene Prefab 的 Animator 没有 Missing Motion
- Timeline Binding 和 Playable Asset 引用完整
- Intro Cutscene 可以在编辑器中正常播放

### 阶段三：Resource 旧目录清理

先迁移活动 VFX，再处理重复 Ani Prefab，最后归档或删除旧资源 Prefab。

退出条件：

- `Assets/Resource` 为空并删除
- 当前 Build Settings 场景不引用 Legacy Prefab
- `PFB_Ani_Blaster_View` 的 Beam Prefab 引用有效
- 旧场景仍可按其保留策略加载，或已经明确删除
- DOTween 初始化行为与迁移前一致

### 阶段四：Scenes

移动正式场景、SubScene、Benchmark 和 Legacy 场景，并同步场景生成数据。

退出条件：

- Build Settings 中三个启用场景路径正确
- `SCN_MainMenu -> SCN_GameLevel` 流程正常
- SubScene 可以加载和烘焙
- Navigation 固定场景验收路径已更新
- 全部场景 Missing Script 数量为 0

### 阶段五：Audio

按 BGM、Ambience、UI 和 Gameplay SFX 移动，不重新编码音频文件。

退出条件：

- AudioManager、Mixer、Prefab 和场景引用完整
- 菜单、环境、脚步、武器和资源音效可以播放
- Git 没有出现非预期二进制内容变化

### 阶段六：Terrain 与 Environment

Terrain 单独迁移，第一轮整体移动，第二轮再按 Terrain Data、Layer、Brush、Vegetation 和 Prefab 拆分。

退出条件：

- 61 个 Terrain 资产 GUID 保持不变
- 113 处既有序列化引用全部有效
- Terrain Layer、树木、材质、纹理和 Collider Authoring 正常
- `SCN_GameLevel` 与 Navigation 烘焙场景地形显示正常
- Client 与 Dedicated Server 构建通过

### 阶段七：生成缓存与规范收尾

1. 删除确认可生成的 `SceneDependencyCache`
2. 增加对应忽略规则
3. 更新项目目录规范和架构文档
4. 运行全项目无旧路径扫描
5. 生成最终目录快照

## 7. 每阶段验收门禁

每个迁移提交至少完成：

1. `git diff --summary` 主要表现为 rename，不出现资产内容大面积重写
2. 所有资产和 `.meta` 成对存在
3. 没有重复 GUID
4. 没有旧路径字符串残留
5. Unity 完整重新导入无新增 Error
6. Scene、Prefab、SO、Animator 和 Timeline 引用检查通过
7. Entities 与 NetCode Source Generator 通过
8. 程序集迁移审计和注释规范检查通过
9. 当前阶段相关的 Client 与 Dedicated Server 构建通过
10. 提交中不包含其他功能修改

## 8. 提交建议

推荐提交顺序：

```text
update: 迁移Navigation脚本目录
update: 收敛Gameplay物理目录
update: 收敛Presentation物理目录
update: 整理Physics Authoring目录
update: 迁移动画与Timeline资源
update: 清理旧Resource资源目录
update: 整理场景目录
update: 整理音频目录
update: 迁移Terrain与环境资源
chore: 清理场景依赖缓存
```

任何阶段出现 Missing Script、GUID 改变、Source Generator 失败、构建场景丢失或大面积二进制重写时，应停止后续迁移并回滚当前阶段提交。
