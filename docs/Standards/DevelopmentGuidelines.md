# AnimarsCatcher 项目开发规范

[返回项目文档总目录](../README.md)

> 版本：1.6
> 更新日期：2026-07-12
> 适用范围：策划、美术、程序、技术美术及使用 AI 辅助开发的参与者

## 1. 使用方式

本规范采用专题文档组织。参与者应阅读总则及与自己工作相关的专题；修改公共 Scene、Prefab、网络协议、Packages 或 ProjectSettings 时，应同时阅读相关全部专题。

规范要求分为：

- **必须**：违反后不得合并到正式分支。
- **建议**：默认遵守，确有理由时在 Review 中说明。
- **可选**：根据模块规模和任务风险选择使用。

新增内容立即遵守规范；存量内容通过独立任务渐进整改，不进行无验证的大规模移动、改名或格式化。

## 2. 专题目录

| 文档 | 适用内容 |
|---|---|
| [01 项目组织与命名](01_ProjectOrganization.md) | 目录、文件归属、第三方内容、C# 与资源命名、存量迁移 |
| [02 代码、DOTS 与 NetCode](02_CodeArchitecture.md) | C#、注释率、asmdef、Entities、多 World、服务器权威和 RPC |
| [03 Unity 内容规范](03_UnityContent.md) | Scene、SubScene、Build Profile、Prefab、SO、资源和渲染设置 |
| [04 Git、协作与 AI](04_GitCollaboration.md) | 分支、提交前缀、Unity Git 要求、提交检查、所有权和 AI 使用 |
| [05 测试与质量门禁](05_QualityGates.md) | 风险等级、测试、构建、发布、Review 和当前项目专项禁区 |
| [06 命名迁移记录](06_NamingMigration.md) | 2026-07-12 存量命名整改范围、验证结果和保留边界 |

## 3. 当前技术基线

- Unity：`6000.2.7f2`
- Entities：实际解析 `1.4.3`
- NetCode：`1.9.0`
- URP：实际解析 `17.2.0`

Unity 版本以 `ProjectSettings/ProjectVersion.txt` 为准，包的实际解析结果以 Unity Package Manager 和 `Packages/packages-lock.json` 为准。

Unity 或核心包升级必须使用独立分支和提交，并验证脚本编译、Baker/SubScene、Host + Client、Ghost 序列化、Build Profile 和主要渲染档位。

## 4. 全员必须遵守的原则

1. 不随意修改其他成员负责的模块；修改公共内容前先同步影响范围。
2. Scene、Prefab、Animator Controller、SO、Packages 和 ProjectSettings 修改后必须由对应负责人 Review。
3. 资产与 `.meta` 必须成对提交、移动和删除，不手工修改 GUID。
4. 一次提交只处理一类相对完整的问题，不混入无关修改。
5. 提交信息使用 `chore:`、`fix:`、`feat:` 或 `update:` 前缀，并具体说明做了什么。
6. 客户端输入和 RPC 均视为不可信；伤害、资源、生成和胜负由服务器权威决定。
7. Runtime 代码不得直接依赖 `UnityEditor`。
8. 不使用 `Obsolete` 目录逃避源码清理，目录名不会阻止 Unity 编译。经负责人批准的性能基线只能放在 `Assets/Scripts/Benchmarks`，正式场景不得默认启用，也不得继续承载新业务。
9. AI 产出由提交者负责，必须 Review、核对当前 API、编译并验证。
10. 提交前确保项目可运行、Console 无新增 Error、Scene/Prefab 已保存，并清理临时对象和调试日志。
11. 项目缩写使用统一写法；公开范围越大越应使用完整名称，短生命周期局部变量只在无歧义时使用常见缩写，不使用 `e` 等含义不明确的单字母名称；Unity、NetCode、NavMesh 等官方名称沿用官方大小写。
12. Inspector `Tooltip` 不重复翻译字段名；仅在补充单位、范围、行为或约束时保留，并使用简短中文且末尾不加句号。
13. 编码、行尾、空格、注释和格式化门禁只处理手写文件；Unity 序列化文本、生成内容、二进制和十六进制数据只检查必要性、完整性和可用性。

## 5. 注释率要求

项目自有业务 C# 源码注释率必须大于等于 **15%**，推荐维持在 **17% 左右**。注释正文统一使用中文，API 和类型名等必要技术名称可以保留英文，正文结尾不使用中文句号或英文句号；公共 API 的 `<summary>` 标签与正文必须分行书写，公共类中的私有实现不使用 XML 文档注释，确需说明时使用中文 `//`。复杂算法还必须说明算法模型、关键不变量、边界条件和重要性能取舍。详细计算范围、例外和防止无意义注释的要求见 [代码、DOTS 与 NetCode 规范](02_CodeArchitecture.md#42-注释率)。

## 6. 规范优先级与维护

当本规范与已确认的模块设计、Git 指南或负责人书面决策冲突时，以最新书面决策为准，并同步修订规范，避免长期存在口头例外。

规范变更应使用独立文档提交。规则必须对应真实风险、重复问题或明确协作成本；已失效或无法执行的规则应及时修订。
