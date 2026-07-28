# 为项目添加主机迁移

> [!NOTE]
> 主机迁移是一项实验性功能，其 API 和实现未来可能发生变化。该功能默认不公开，若要启用，请在项目设置 __Player__ 选项卡的 __Scripting Define Symbols__ 中添加 `ENABLE_HOST_MIGRATION` 定义

了解为项目添加主机迁移所涉及的要求、系统和集成

| **主题**                        | **说明**                         |
| :------------------------------ | :------------------------------- |
| **[主机迁移要求](host-migration-requirements.md)** | 了解在项目中使用主机迁移的要求及受支持的平台 |
| **[主机迁移设计注意事项](host-migration-considerations.md)** | 支持将服务器数据迁移到新服务器的项目需要考虑特定设计问题 |
| **[主机迁移系统与数据](host-migration-systems.md)** | 在项目中设置主机迁移系统，为客户端托管的网络会话启用主机迁移 |
| **[Lobby 与 Relay 集成](lobby-relay-integration.md)** | 与 Unity Lobby 和 Unity Relay 集成，在 Netcode for Entities 中启用主机迁移 |
