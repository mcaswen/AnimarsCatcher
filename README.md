# AnimarsCatcher

AnimarsCatcher 是一个基于 Unity DOTS、Entities 与 NetCode for Entities 的多人 RTS 原型。玩法同时使用 ECS World 和 GameObject 表现层：服务器负责权威规则，客户端负责输入、预测和画面表现，Scene、Prefab 与 Authoring 负责提供烘焙数据

## 当前状态

- Unity 版本为 `6000.2.7f2`
- `Assets/Scripts` 共有 331 个 C# 文件，全部归属 15 个自定义程序集，项目 asmref 为 0
- Navigation 已完成 R1～R6 架构重构和算法复审，Stage 1～5 自动验收全部通过
- Grid 后端已具备静态烘焙、普通 A*、HPA Corridor、局部 Flow Field、动态 Overlay、Squad 移动和自适应矩形阵型
- 阶段五正式场景验收、阶段六局部避碰与世界碰撞、阶段七资源迁移和正式后端切换仍未完成；未指定启动参数时继续使用 Legacy NavMesh

## 文档入口

- [项目文档总目录](docs/README.md)
- [项目架构总览](docs/Architecture/README.md)
- [开发规范](docs/Standards/DevelopmentGuidelines.md)
- [Navigation R1～R6 执行报告](docs/Architecture/Reports/NavigationRefactor-Execution-20260820.md)

当前 Build Settings 启用 `SCN_MainMenu`、`SCN_GameLevel_SubScene` 和 `SCN_GameLevel`。正式流程从主菜单进入游戏场景，SubScene 提供 ECS 场景数据
