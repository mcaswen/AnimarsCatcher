# Asteroids 中的主机迁移

此示例以 Asteroids 示例为基础，演示 Netcode for Entities 中的一种[主机迁移](host-migration.md)实现

有关 Netcode for Entities 主机迁移的一般信息，请参阅[主机迁移页面](host-migration.md)

<a id="requirements"></a>
## 要求

* Asteroids 示例项目要求使用 Unity 6，最低版本为 6.0.23f1，因为它使用了 [Dedicated Server](https://docs.unity3d.com/Packages/com.unity.dedicated-server@latest?subfolder=/manual/index.html) 和 [Multiplayer Play Mode](https://docs-multiplayer.unity3d.com/mppm/current/about/) 包中的新功能。但是，主机迁移 API 本身与 Netcode for Entities 包一样，也可以在 Unity 2022.3 中运行
* 此示例项目需要关联到 Unity Cloud Dashboard 中的项目，并配置为使用 [Player Authentication](https://docs.unity.com/ugs/en-us/manual/authentication/manual/get-started)、[Relay](https://docs.unity.com/ugs/en-us/manual/relay/manual/get-started) 和 [Lobby](https://docs.unity.com/ugs/en-us/manual/lobby/manual/get-started) 服务

<a id="sample-steps"></a>
## 示例步骤

1. 新建 Unity 项目，并按照[要求](#requirements)所述将其关联到项目 ID
    * 可以选择在 Unity Cloud Dashboard 中打开 **Lobby** > **Config**，将 **Active Lifespan** 改为 120 秒、**Disconnect Removal Timeout** 改为 60 秒、**Disconnect Host Migration Time** 改为 5 秒
2. 通过 **Window** > **Multiplayer Play Mode** 打开 Multiplayer Play Mode 窗口，启动两个虚拟 Player 实例，并将二者的角色都设为 **Client and Server**
3. 在 _Frontend_ 场景中进入 Play 模式，该场景位于 _Assets/Samples/HelloNetcode/1_Basics/01_BootstrapAndFrontend_。在下拉列表中选择 _Asteroids_ 示例，再启用 **Enable Host Migration** 开关
    * 如果修改 Lobby 名称，请确保所有其他实例也使用相同名称
    * 按**空格键**生成玩家飞船
4. 选择编辑器或某个虚拟 Player 作为初始主机，并单击 **Start Client & Server**
5. 等待主机迁移统计信息出现在右下角。该信息出现后，表示 Lobby 连接已经可以处理主机迁移
    * 右下角还会显示当前连接实例是服务器还是客户端
6. 在其他实例中选择 **Join Existing Game**
7. 在主机上单击角落的 **Return To Main Menu** 按钮，退出游戏和 Lobby 并触发主机迁移
    * 若要通过超时触发主机迁移，最好使用独立构建并终止 Player
8. 其他实例之一会成为新主机，并在角落显示迁移更新统计信息。其余实例会自动加入新主机
9. 主机数据应已在主机之间完成迁移。全部小行星和飞船的位置应与迁移前相同，飞船颜色也应保持不变，并在飞船被销毁后重新生成时继续保持一致

<a id="ensuring-that-state-is-migrated-properly-in-asteroids"></a>
## 确保 Asteroids 状态正确迁移

* 每艘玩家飞船都会应用一种颜色。为了保证该颜色在主机迁移前后保持一致，需要将玩家颜色与连接关联，并使用一个特殊 Ghost 预制体承载需要迁移的主机数据
  * 服务器接受连接时，会为每个连接分配 `PlayerColor` 组件。生成由该连接拥有的玩家飞船时，会将颜色组件添加到飞船上
  * 发生迁移时，连接实体上的全部用户组件都会一同迁移，包括 `PlayerColor` 组件
  * 特殊的 `HostConfig` Ghost 实体上带有仅服务器使用的 `PlayerColorNext` Ghost 组件。该 Ghost 只包含服务器数据，因此没有需要复制给客户端的内容。`HostConfig` 实体保存下一条客户端连接将分配的颜色，该整数从 1 开始递增，并在 12 条连接后循环。请参阅辅助类 [`Unity.NetCode.NetworkIdDebugColorUtility`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkIdDebugColorUtility.html)
* 客户端检测到重新连接的连接后，会在 `LevelComponent` 创建完成时为其添加 [`NetworkStreamInGame`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamInGame.html) 组件，使连接进入游戏。这表示客户端已按照服务器命令完成关卡配置，可以开始游戏。请参阅 `Asteroids.Client.HostMigrationSystem`
* 当小行星数量低于关卡配置的上限时，服务器会自动生成额外小行星。主机迁移期间，该系统需要暂停并等待主机迁移数据部署完成；否则，它会先生成完整数量的小行星，随后主机迁移流程又会生成主机数据中包含的小行星。检测到 [`HostMigrationInProgress`](host-migration-api.md) 组件时，系统会退出更新循环以实现暂停

## 其他资源

* [主机迁移](host-migration.md)
