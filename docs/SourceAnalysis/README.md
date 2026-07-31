# 第三方源码分析

[返回项目文档总目录](../README.md)

本目录用于记录已经嵌入仓库、需要长期阅读和维护的第三方源码。这里描述第三方包自身的实现事实、扩展点和风险，不替代项目架构文档，也不把第三方内部类型当作项目公共 API。

当前专题：

1. [NetCode for Entities 1.9.0 架构分析](NetCodeForEntities/01_Architecture.md)
2. [NetCode 时间线与 Tick 模型](NetCodeForEntities/02_TimelineAndTickModel.md)
3. [NetCode 快照与基线模型](NetCodeForEntities/03_SnapshotsAndBaselines.md)

后续分析文档应按包或框架建立独立子目录，并在文档开头记录版本、源码位置和阅读范围。包升级后必须重新核对关键链路，不直接沿用旧版本结论。
