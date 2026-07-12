# 存量命名整改记录

[返回开发规范总目录](DevelopmentGuidelines.md)

## 1. 整改范围

本次整改日期为 2026-07-12，范围为项目自有的活跃代码、Scene、Prefab、动画、材质、纹理、Shader、音频和地形资源。`Assets/Plugins`、`Assets/Samples`、`Assets/TextMesh Pro`、`Assets/Timeline`、明显第三方 Shader 及 Unity 生成内容不纳入批量改名。

本次通过 Unity `AssetDatabase` 分批完成 198 项移动或改名：171 项主体资源迁移、12 项目录与配套资源迁移，以及 15 项 BGM 和历史 Prefab 收口迁移。另有 3 个 Terrain Layer 和 1 个动画 FBX 采用资产与 `.meta` 成对移动，共执行 202 项显式路径迁移。最终范围包括：

- 7 个 Scene 和 3 个配套烘焙数据目录；
- 11 个 Animation Clip 和 4 个 Animator Controller；
- 18 个 Material、52 个 Texture、5 个项目 Shader/ShaderGraph；
- 17 个项目音频；
- 57 个原本没有 `PFB_` 前缀的项目 Prefab；
- 12 个已有前缀但模块顺序、大小写或单词拼写不一致的 Prefab。

其中 Prefab 共整改 69 个。文件夹迁移会连带内部资产改变路径，最终共有 238 个已跟踪 `.meta` GUID 从旧路径迁移到新路径。主要目录调整如下：

```text
Assets/Art/Audios                 -> Assets/Audio
Assets/Art/Ani_texture            -> Assets/Art/Textures/Anis
Assets/Art/FX                     -> Assets/Art/VFX
Assets/Prefabs/Local/PFX          -> Assets/Prefabs/Local/VFX
Assets/Prefabs/Network/Anis_Mono  -> Assets/Prefabs/Network/Anis/Mono
```

## 2. 代码命名

已完成以下低风险整改：

- 18 个 C# 文件与主要类型名称对齐；
- 修复 `Formution`、`Comsume`、`KeyBoard`、`BlackBoard`、`Serverui` 等明显拼写和大小写问题；
- FSM 常量、状态 ID、条件 ID、动作 ID 和方法统一为 PascalCase；
- 活跃代码中的非序列化私有字段统一为 `_camelCase`；
- 部分 Inspector 字段使用 `FormerlySerializedAs` 迁移到 `_camelCase`，保留已有 Scene/Prefab 序列化值；
- 场景字符串统一为 `SCN_MainMenu`、`SCN_GameLevel`，并同步 Build Settings 和 Prefab 序列化值。

## 3. 验证结果

1. 迁移前后均使用 Unity `6000.2.7f2` 完成脚本重新编译。
2. `EditorBuildSettings` 已指向 `SCN_MainMenu`、`SCN_GameLevel_SubScene` 和 `SCN_GameLevel`。
3. 238 个发生路径变化的已跟踪 `.meta` GUID 均在新路径唯一存在；当前工作树没有重复 GUID。
4. 与基线相比仅缺少整改前已经删除的 `Assets/Scripts/Animation.meta`，本次迁移没有造成额外 GUID 丢失。
5. Scene、Prefab 和 SubScene 显式字符串引用已扫描并同步。

## 4. 保留例外

以下内容不是遗漏，不得继续无差别批量改名：

1. `Assets/Resource` 下 10 个旧 Prefab 已统一文件名，但该目录不是 Unity 特殊目录 `Resources`；旧加载流程仍应迁移到 Registry 或 Inspector 引用。
2. 5 个 BGM 已使用项目命名，但仍需补齐来源、许可证和署名记录；改名不改变原授权义务：

```text
Shadows and flames             -> BGM_ShadowsAndFlames
Haven (seamless)               -> BGM_Haven
Glorious (seamless)            -> BGM_Glorious
Exploring darkness (seamless)  -> BGM_ExploringDarkness
Scroll of the wind walker      -> BGM_ScrollOfTheWindWalker
```
3. TMP SDF、NavMesh、Lightmap、Build Profile 和物理材质模板等生成或工具资产遵循 Unity 工具链名称，不机械添加业务前缀。
4. Ghost Variant 和 RPC 类型不会在纯命名任务中改名，避免改变 NetCode 类型哈希和协议兼容性。
5. 原 `Obsolete` 旧脚本已移出 Unity 项目；后续废弃源码直接删除并依赖 Git 历史，不为保留旧代码投入批量改名成本。

## 5. 后续任务

1. 按模块迁移缺少命名空间的存量代码；MonoBehaviour 使用 `MovedFrom`，NetCode 类型同步升级 Client/Server 协议。
2. 分批把仍为 public 的 MonoBehaviour/Authoring Inspector 字段迁移为 `[SerializeField] private`，使用 `FormerlySerializedAs` 保留数据。
3. 清理 `Assets/Resource` 旧流程，并确认 10 个已规范命名的旧 Prefab 是删除、归档还是接入正式 Registry。
4. 核对重复的 Plant FBX/Texture，确认引用后保留唯一权威资源。
5. 由场景负责人检查正式场景中历史实例名覆盖；不要为清理层级名称制造一次超大 Scene YAML 提交。
6. 修复 Windows Build Profile 覆盖全局场景列表但列表为空的问题，并执行目标平台构建。

本次整改应作为独立迁移提交 Review。需要回滚时整体回滚迁移提交，不手工重新生成 `.meta` 或 GUID。
