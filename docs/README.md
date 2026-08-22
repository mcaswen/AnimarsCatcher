# AnimarsCatcher 项目文档

- [开发规范总目录](Standards/DevelopmentGuidelines.md) 位于 `Standards`，包含开发、命名、Unity、Git、测试和质量门禁规范
- [项目架构总览](Architecture/README.md) 位于 `Architecture`，按 01～16 的连续编号描述当前模块、ECS、NetCode、关键链路、迁移记录、Navigation 重构结果和万人群体移动规划
- [第三方源码分析](SourceAnalysis/README.md) 位于 `SourceAnalysis`，记录嵌入源码的架构、关键链路、扩展点和升级风险

## 阅读建议

1. 新成员先阅读开发规范总目录和项目架构总览
2. 修改 ECS 或网络逻辑时，继续阅读数据模型、网络边界和关键链路文档
3. 修改 Navigation 时，先看 08～10 的目标与功能阶段，再看 14～15 的现行结构与 R1～R6 执行结果；实施万人群体移动时继续阅读 16
4. 修改 Scene、Prefab、Authoring 或 Hybrid View 时，同时阅读 Unity 内容规范和关键类设计
5. 架构文档描述当前仓库事实；开发规范定义新增和修改代码必须遵守的约束
6. 需要理解第三方包内部行为或准备升级包时，阅读对应源码分析专题，并回到当前嵌入源码核对实现

## 维护原则

1. 规则变化更新 `Standards`，实现边界变化更新 `Architecture`
2. 新增重要 Entity、RPC、World、系统链路或跨层桥接时，同步更新对应架构文档
3. `Architecture` 根目录的编号文档保持连续；新增文档时同步更新总览和所有交叉引用
4. 文档中的路径统一使用仓库相对路径，确保在分支、工作区和代码托管平台中可复用
5. 第三方包升级后同步复核 `SourceAnalysis` 中对应专题，不把旧版本内部实现当作稳定 API
