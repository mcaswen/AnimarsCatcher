# 主机迁移要求

了解在项目中使用主机迁移的要求及受支持的平台

确认项目满足这些要求后，即可继续[在项目中设置主机迁移系统](host-migration-systems.md)

<a id="requirements"></a>
## 要求

在项目中使用主机迁移之前，需要具备以下条件：

- 拥有有效许可证的活跃 Unity 账户
- Unity Hub
- 受支持版本的 Unity 6 编辑器
- Unity Cloud Dashboard 访问权限

<a id="unity-project-setup"></a>
## Unity 项目设置

可以新建 Unity 项目，也可以使用 [Asteroids 示例](host-migration-sample.md)快速开始测试 Netcode for Entities 的主机迁移。创建新项目时，勾选 **Connect to Unity Cloud**，将项目连接到 Unity Cloud

<a id="packages"></a>
## 包

- Netcode for Entities (com.unity.netcode)：1.5.0-exp.100
- Multiplayer Services SDK (com.unity.services.multiplayer)：1.2.0-exp.2

<a id="services-and-costs"></a>
## 服务与费用

主机迁移协调与状态传输由 [Unity Lobby](https://docs.unity.com/ugs/en-us/manual/lobby/manual/unity-lobby-service) 服务提供，不收取额外费用。上传和下载主机迁移数据所使用的带宽不计费，也不计入免费层额度或付费层用量

[Unity Relay](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction) 服务与 Lobby 服务配合使用，通过转发游戏会话中各方之间的消息保证可靠连接。Relay 按连接时间和出站带宽计费。免费层允许每月平均最多 50 个并发用户，并为每个并发用户提供 3GiB 带宽

详细信息请访问 [Unity Gaming Services 定价页面](https://unity.com/products/gaming-services/pricing)

<a id="supported-platforms"></a>
## 支持的平台

* 桌面：Windows、macOS、Linux
* 移动端：Android、iOS
* 主机：Nintendo Switch、Xbox、PlayStation 4、PlayStation 5
* 专用服务器：Linux、Windows、macOS
* Web：WebGL

## 其他资源

* [主机迁移简介](host-migration-intro.md)
* [限制与已知问题](host-migration-limitations.md)
* [Asteroids 主机迁移示例](host-migration-sample.md)
* [Unity Lobby 文档](https://docs.unity.com/ugs/en-us/manual/lobby/manual/unity-lobby-service)
* [Unity Relay 文档](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction)
