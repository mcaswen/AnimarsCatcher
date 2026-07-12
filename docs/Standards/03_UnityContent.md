# Unity 场景、Prefab 与资源规范

[返回开发规范总目录](DevelopmentGuidelines.md)

## 1. 场景与 Build Profile

1. 正式构建场景必须由一个明确的 Build Profile 或全局场景列表管理，不允许两套列表长期不一致。
2. 启动场景必须固定、可验证，并位于场景列表首位。
3. 被父场景 AutoLoad 的 SubScene 不应同时作为独立 Player 入口加入构建，除非存在明确的独立加载需求。
4. 主场景和 SubScene 不得同时保留相同 Terrain、灯光、碰撞体、基地或地图对象，除非采用经过说明和验证的双表现方案。
5. 测试场景放入 `Scenes/Dev` 或 `TestScenes`，默认不进入正式 Build Profile。
6. 场景加载名称不得散落在多个脚本中，应由常量、配置或场景目录服务统一管理。
7. 修改场景名时必须同步检查字符串加载、Build Profile、Timeline、测试和文档。

## 2. 场景层级

1. 公共对象放在职责清晰的根节点下，例如 `Bootstrap`、`Managers`、`Environment`、`Gameplay`、`UI`、`Debug`。
2. 不保留默认名称 `GameObject` 或无职责的空节点。
3. 启动、Mono 表现、服务器 Authoring 和 Entities SubScene 的职责必须清楚。
4. 测试对象、禁用废弃对象和无效引用必须在正式提交前清理。
5. 场景保存前检查当前激活状态、Prefab Override、光照数据、NavMesh 和 SubScene AutoLoad 设置。

## 3. 场景协作

1. 同一时间一个共享场景应有明确负责人。
2. 修改共享场景前，在团队渠道同步修改范围和预计完成时间。
3. 禁止通过复制正式场景并长期并行开发来规避冲突。
4. 需要多人并行时，优先拆分 Additive Scene、SubScene、Prefab 或独立 UI Canvas。
5. 临时场景副本必须明确标识，不加入正式构建，验证结束后删除。
6. 场景冲突不能简单选择一方文件覆盖，必须由相关负责人在 Unity 中逐项确认。

## 4. Prefab

1. 可复用对象必须制作 Prefab。
2. 公共对象修改优先回写 Prefab，不只修改某个场景实例。
3. `Prefabs/Local` 与 `Prefabs/Network` 职责必须分开。
4. Network Prefab 应明确区分 Entity/Ghost 数据、客户端 View 和服务器专用内容。
5. 修改共享 Prefab 或 Animator Controller 前必须先同步负责人。
6. 提交前检查所有 Prefab Override，并明确 Apply 或 Revert。
7. 优先使用 Prefab Variant 表达稳定差异，不复制多个近似 Prefab。
8. 不随意 Unpack Prefab；确需 Unpack 时在提交说明中解释原因。
9. Prefab 不得包含 Missing Script、失效引用或无用途调试组件。
10. View Prefab 不直接包含服务器权威业务逻辑。

## 5. ScriptableObject

1. ScriptableObject 主要承载静态配置和策划数据。
2. 对局运行状态不得直接写回配置资产。
3. 每类配置应有明确所有者、唯一 Id 和校验逻辑。
4. 配置引用的 Prefab、Scene 和资源必须在编辑器或构建前验证。
5. 修改公共配置字段时必须同步使用方，并提供默认值或迁移方案。

## 6. 资源导入

### 6.1 通用要求

1. 源文件和运行时资源按模块约定分开存放。
2. 不重复导入同一资源，不保留未引用的大型候选文件在正式目录中。
3. 替换资源时尽量保留 GUID；无法保留时必须完整检查引用。
4. 导入后检查 Scale、Rig、Compression、Read/Write、MipMap 和平台覆盖设置。
5. 第三方及 AI 生成资源必须记录来源、许可证、可商用状态和署名要求。

### 6.2 纹理与模型

1. 纹理尺寸、格式和压缩按目标平台设置，不使用无必要的超高分辨率。
2. Normal Map 必须设置正确的 Texture Type。
3. 不需要 CPU 读取的纹理和 Mesh 关闭 Read/Write。
4. 模型单位、轴向、Rig、Avatar 和材质导入方式应保持一致。
5. 可复用材质使用独立 Material，不在大量 Prefab 中生成重复材质。

### 6.3 音频

1. 长 BGM 使用 Streaming 或经过验证的压缩加载方式。
2. 短 SFX 根据使用频率选择 Decompress On Load 或 Compressed In Memory。
3. 不让未引用的大型 WAV 长期进入 Git 主仓库。
4. 音量通过 AudioMixer 统一管理，不在多个脚本中直接写死。

### 6.4 Shader 与渲染配置

1. Shader 的 RenderPipeline、RenderType 和 Queue 标签必须使用 Unity 正确字段。
2. Renderer Feature 必须记录依赖的 Layer、Depth Texture、Opaque Texture 和渲染顺序。
3. 修改 URP Asset、Renderer、Quality Settings 或 Volume Profile 属于共享配置变更，必须 Review。
4. Shader Variant 和关键材质必须在目标质量档位及 Player Build 中验证。

## 7. Unity 特殊目录与第三方资源

1. `Resources` 只有完全匹配该名称时才具备 Unity 特殊语义，`Resource` 不等价。
2. 尽量不新增 `Resources.Load`；优先使用显式引用和 Registry。
3. UPM 包保留在 `Packages`。
4. 已有 `Plugins`、Samples、TextMesh Pro 和厂商目录不得未经验证强行迁移到 `ThirdParty`。
5. 修改第三方资源导入设置前确认是否会影响更新和其他平台。

## 8. Unity 项目设置

1. 使用 `ProjectSettings/ProjectVersion.txt` 指定的 Unity 版本。
2. Asset Serialization 必须保持 Force Text，Version Control 必须保持 Visible Meta Files。
3. 修改 `ProjectSettings`、`Packages`、Build Profile、Ghost 配置和输入设置必须说明全项目影响。
4. Unity 或核心包升级使用独立分支和提交，不与普通功能开发混合。
5. 包的实际解析状态以 Unity Package Manager 和 `packages-lock.json` 为准。
