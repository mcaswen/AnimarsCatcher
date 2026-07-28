# Netcode Project Settings 参考

Netcode for Entities 使用 [Entities](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/index.html) 的 **DOTS Settings** 类型定义 Netcode 专用设置。若要打开这些项目设置，请前往 **Edit** > **Project Settings** > **Entities**

<a id="netcode-client-target"></a>
## Netcode Client Target

**Netcode Client Target** 下拉菜单决定最终客户端构建是否支持在同一进程内托管服务器 World

| Netcode Client Target | 使用场景 |
|-----------------------|----------|
| `ClientAndServer` | 用户可以通过 UI 在主游戏可执行文件中托管自己的服务器。调用 `ClientServerBootstrap.CreateServerWorld` 可以正常工作 |
| `ClientOnly` | 只有开发者能够托管服务器。使用此选项可随游戏可执行文件一同发布 DGS（专用游戏服务器）可执行文件。游戏客户端构建使用 `ClientOnly`，DGS 构建会自动使用 `ClientAndServer`。玩家无法使用服务器托管功能，调用 `ClientServerBootstrap.CreateServerWorld` 会抛出 `NotSupportedException` |

**Build Type** 设置只对非 DGS 构建目标有效。独立平台、主机和移动端构建都支持客户端托管服务器

| Build Type            | Netcode Client Target | 定义 |
|-----------------------|-----------------------|------|
| Standalone Client     | `ClientAndServer`     | 不设置 `UNITY_CLIENT` 和 `UNITY_SERVER`，无论构建出的 Player 还是编辑器内均不设置 |
| Standalone Client     | `ClientOnly`          | 在构建中设置 `UNITY_CLIENT`，编辑器内不设置 |
| Dedicated Game Server | N/A                   | 在构建中设置 `UNITY_SERVER`，编辑器内不设置 |

对于任一构建类型，都可以在 DOTS 项目设置中指定特定烘焙过滤器，如下一节所述

<a id="excluded-baking-system-assemblies"></a>
### Excluded Baking System Assemblies

若要构建独立服务器，需要切换到 `Dedicated Server` 平台。构建服务器时会自动设置 `UNITY_SERVER` 定义，编辑器内也会自动设置。DOTS 项目设置会使用服务器构建类型对应的设置来反映这一变化

<a id="additional-scripting-defines"></a>
### Additional Scripting Defines

使用以下脚本定义，通过 `Excluded Baking System Assemblies` 和 `Additional Scripting Defines` 为编辑器与构建确定特定模式的烘焙设置，例如包含或排除特定 C# 程序集

| 设置                                  | 说明 |
|---------------------------------------|------|
| **Netcode Client Target**             | 决定最终客户端构建是否支持作为服务器托管游戏 |
| **Excluded Baking System Assemblies** | 添加需要从烘焙系统中排除的程序集定义资源，可以分别为客户端和服务器配置 |
| **Additional Scripting Defines**      | 添加额外的[脚本定义](https://docs.unity3d.com/Manual/CustomScriptingSymbols.html)，从编译中排除特定客户端或服务器代码 |

<a id="netcodeconfig-scriptableobject"></a>
## `NetCodeConfig` ScriptableObject

Netcode for Entities 提供名为 `NetCodeConfig` 的 [ScriptableObject](https://docs.unity3d.com/Manual/class-ScriptableObject.html)，无需编写 C# 即可修改 `ClientServerTickRate`、`ClientTickRate`、`GhostSendSystemData` 和 Unity Transport 的 `NetworkConfigParameter` 参数。它还在 **Edit** > **Project Settings** > **Multiplayer** 下提供专用的 Netcode for Entities 项目设置页面。各属性的详细信息请参阅 [`NetCodeConfig` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetCodeConfig.html)

还可以参阅 [`ClientServerTickRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html)、[`ClientTickRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientTickRate.html)、[GhostSendSystemData](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostSendSystemData.html) 和 [`NetworkConfigParameter`](https://docs.unity3d.com/Packages/com.unity.transport@latest/index.html?subfolder=/api/Unity.Networking.Transport.NetworkConfigParameter.html) 的 API 文档

<a id="using-netcodeconfig"></a>
### 使用 `NetCodeConfig`

1. 通过 Unity 的 **Create** 菜单、**Multiplayer** 菜单或 **Project Settings** 辅助按钮创建 `NetCodeConfig` ScriptableObject。默认值就是推荐值
2. 打开 **Multiplayer Project Settings** 窗口，将该 ScriptableObject 设为全局配置
    * **警告**：此操作可能导致项目出现运行时错误，因为该配置会覆盖用户代码直接对这些单例组件进行的添加、移除或修改
3. 修改所需设置。大多数字段支持运行时实时调整，不支持的字段会在 Play 模式期间禁用

## 其他资源

* [Entities Project Settings 参考](https://docs.unity3d.com/Packages/com.unity.entities@latest/index.html?subfolder=/manual/editor-project-settings.html)
