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
│   ├── AnimationControllers/
│   ├── Materials/
│   ├── Models/
│   ├── Sprites/
│   ├── Textures/
│   └── VFX/
├── Audio/
│   ├── BGM/
│   ├── SFX/
│   └── Mixers/
├── Prefabs/
│   ├── Local/
│   └── Network/
├── Scenes/
│   ├── Bootstrap/
│   ├── Gameplay/
│   ├── SubScenes/
│   └── Dev/
├── Scripts/
│   ├── Anis/
│   ├── Player/
│   ├── Netcode/
│   ├── Resource/
│   ├── Base/
│   ├── Health/
│   ├── Camp/
│   ├── UI/
│   ├── Shared/
│   ├── Editor/
│   └── Tools/
├── SO/
├── Settings/
├── Shaders/
└── ThirdParty/

Packages/
ProjectSettings/
docs/
```

脚本继续采用当前项目的领域优先结构，不把全部系统集中到顶层 `Systems`。领域内部可按下列职责划分：

```text
Scripts/<Domain>/<Feature>/
├── Components/
├── Authoring/
├── Systems/
│   ├── Client/
│   ├── Server/
│   └── Common/
├── Presentation/
└── Utilities/
```

不得为了形式完整创建空目录。

## 3. 存放要求

1. 正式场景统一放在 `Assets/Scenes`。
2. 业务 C# 源码统一放在 `Assets/Scripts`。
3. 使用 `UnityEditor` 的代码只能位于 `Editor` 目录、Editor-only asmdef 或正确的条件编译区域。
4. 测试脚本放在独立测试目录和测试 asmdef 中。
5. ScriptableObject 配置放在 `Assets/SO/<Domain>`，或模块明确约定的数据目录。
6. `Obsolete` 不是 Unity 编译排除目录。废弃源码应删除并依赖 Git 历史，或移至 Unity 项目外归档。
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
6. RPC 类型使用 `Rpc` 后缀，标识符使用 `Id`，例如 `StartGameRpc`、`NetworkId`。
7. `Manager` 仅用于确实管理生命周期或跨模块协调的类型。
8. DOTS 组件、RPC 和 Buffer Element 等纯数据结构允许使用 public 字段。

新业务代码使用 `AnimarsCatcher` 根命名空间，例如：

```text
AnimarsCatcher.Animars.Combat
AnimarsCatcher.Netcode.Lobby
AnimarsCatcher.Resource.Carrying
AnimarsCatcher.UI.Gameplay
```

命名空间以稳定业务领域为主，不要求逐级复制物理目录。存量全局命名空间通过专项任务渐进迁移。

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
