# AnimarsCatcher 项目文档

- [开发规范总目录](Standards/DevelopmentGuidelines.md) 位于 `Standards`，包含开发、命名、Unity、Git、测试和质量门禁规范
- [项目架构总览](Architecture/README.md) 位于 `Architecture`，描述当前模块、ECS、NetCode、关键链路和关键类设计

## 阅读建议

1. 新成员先阅读开发规范总目录和项目架构总览。
2. 修改 ECS 或网络逻辑时，继续阅读数据模型、网络边界和关键链路文档。
3. 修改 Scene、Prefab、Authoring 或 Hybrid View 时，同时阅读 Unity 内容规范和关键类设计。
4. 架构文档描述当前仓库事实；开发规范定义新增和修改代码必须遵守的约束。

## 维护原则

1. 规则变化更新 `Standards`，实现边界变化更新 `Architecture`。
2. 新增重要 Entity、RPC、World、系统链路或跨层桥接时，同步更新对应架构文档。
3. 文档中的路径统一使用仓库相对路径，确保在分支、工作区和代码托管平台中可复用。
