# Player 与 Networking 程序集迁移

[返回架构总览](README.md)

> 状态：阶段四已完成
>
> 实施日期：2026-07-19
>
> 新增程序集：`AnimarsCatcher.Player`、`AnimarsCatcher.Player.Editor`、`AnimarsCatcher.Networking`

## 1. 阶段结果

阶段四把玩家输入、预测移动、KCC、相机运行时和角色 View 归入 Player 编译边界，把 World 创建、连接、监听、大厅、InGame、Spawn、网络协议和网络工具归入 Networking 编译边界。

迁移后的脚本归属如下：

- `AnimarsCatcher.Player` 包含 45 个运行时或 Authoring 脚本
- `AnimarsCatcher.Player.Editor` 包含 1 个 Input System 编辑器检查脚本
- `AnimarsCatcher.Networking` 包含 32 个运行时、Authoring 或条件编译调试脚本
- 三个程序集全部使用固定 asmdef GUID 和统一根命名空间

Player 不引用 Networking。Networking 可以引用 Player 的稳定 ECS 数据和角色类型，但不能调用 Player 的具体 System。两者之间没有双向程序集引用。

## 2. Player 边界

Player 负责：

- 设备输入采集与输入锁状态
- `InputCommand` 和固定 Tick 输入事件
- Fixed 与 Orbit 相机控制
- 第三人称 KCC 与简化角色预测移动
- Player Character、Camera 和 Avatar View 的 Authoring 与运行时组件
- 客户端主相机同步和过场相机占用状态

Player 当前只依赖 `AnimarsCatcher.Gameplay` 和必要 Unity Package。它不引用 Networking、Mono、UI、Legacy Benchmark 或仍位于 `Assembly-CSharp` 的 Physics 类型。

原 `Physics/CharacterBoxAuthoring` 已移动到 Player 角色控制 Authoring 目录。`CharacterBoxInfo` 只服务于 Player 的简化角色碰撞移动，不再被当作无归属的公共 Physics 数据。

原 `PlayerInputLockFromUISystem` 直接订阅 Mono UI 事件，因此不属于 Player 运行时。该系统已移动到 `MonoBehaviour/Input`，由表现层依赖 Player 的 `PlayerInputLockState`。

`ClientCinematicState` 只控制 Player 主相机是否让出写入权，已从 Mono Global 移到 Player。开场演出仍可从表现层修改该状态，但 Player 不再反向引用表现层。

## 3. Networking 边界

Networking 负责：

- `CustomBootstrap` 和网络运行角色检测
- Client、Server 与 Thin Client World 创建
- 服务器监听与客户端连接请求
- LAN 面板使用的 NetCode 门面
- 大厅身份、开局、进入 InGame 和调试直入协议
- 玩家 Ghost Prefab 注册、出生点配置和服务器角色创建
- Ghost Variant、连接探针和调试 HUD

`NetworkRuntimeRole` 已从 Mono Global 移入 Networking Bootstrap。LAN 和菜单代码从上层读取该状态，Bootstrap 不再依赖 Mono 命名空间。

Networking 允许依赖：

- `AnimarsCatcher.Gameplay.Contracts`
- `AnimarsCatcher.Gameplay`
- `AnimarsCatcher.Player`
- NetCode、Entities、Physics、Character Controller 和 Transport 等必要 Unity Package

Networking 不引用 Mono 或 UI。`Unity.Physics` 是 NetCode 为 `KinematicCharacterBody` 生成 Ghost Variant 序列化器时需要的显式编译依赖，不能只依赖 Character Controller 的传递引用。

## 4. 网络与表现桥接

迁移前，多个 Networking System 直接调用 `NetworkUIEventBridge`、`GlobalLoadingUI` 和 `ClientCinematicState`。这会形成 Networking 到 Mono 的反向依赖，使 Networking 无法成为独立程序集。

阶段四新增 `NetworkLifecycleNotifications`，以短生命周期 ECS Entity 发布三类网络生命周期通知：

- 大厅成员加入
- 对局开始
- 客户端权威场景加载请求

Networking System 只负责产生这些通知，不知道具体面板、加载遮罩或 UnityEvent。

Mono Global 中新增 `NetworkPresentationBridgeSystem`。它在对应 Client 或 Server World 中消费通知 Entity，并完成以下适配：

- 把大厅成员通知转发为现有 `NetworkUIEventBridge` 事件
- 把对局开始通知转发给房间 UI
- 优先调用 `GlobalLoadingUI` 执行带遮罩的异步场景切换
- 加载界面缺失时回退到直接加载场景
- 场景加载完成后设置 Player 的开场演出状态

该方向保持为 `Presentation -> Networking / Player`。以后迁移 Presentation 时可以替换桥接实现，不需要修改网络协议 System。

## 5. 命名空间与序列化

Player 统一使用 `AnimarsCatcher.Player`，Player Editor 使用 `AnimarsCatcher.Player.Editor`，Networking 统一使用 `AnimarsCatcher.Networking`。

已有 MonoBehaviour、Authoring 和 Baker 脚本移动时均保留原 `.meta` GUID：

- `CharacterBoxAuthoring`
- `ClientCinematicState`
- `NetworkRunRole`
- `PlayerInputLockFromUISystem`

新建文件使用固定 `.meta` GUID。阶段四没有通过重建 `.meta` 或删除资源引用解决程序集迁移问题。

`AnimarsCatcher.Mono.Input` 曾在首次编译中遮蔽 `UnityEngine.Input`，因此输入锁桥接命名空间最终使用 `AnimarsCatcher.Mono.Bridges`。命名空间不得与常用 Unity 类型形成模糊解析。

## 6. 审计结果

阶段四完成后：

- 自有脚本为 267 个
- 项目程序集定义为 7 个
- asmref 保持 6 个
- Player、Player Editor 和 Networking 命名空间覆盖率为 100%
- 全局命名空间脚本从 129 个降到 56 个
- 候选跨模块依赖当前为 22 条，其中新增的 Editor 到 Gameplay、Player 和 Networking 引用来自阶段四自动验收入口
- 直接双向依赖从 2 组降到 1 组
- 剩余双向依赖只有 Mono 与 UI，属于阶段五 Presentation 范围
- 程序集依赖边界违规为 0
- 严重审计问题为 0
- 全局命名空间基线过期条目为 0

## 7. 验证范围

阶段四使用完整物理副本完成验证，覆盖：

- Unity 完整导入和脚本编译
- Player、Player Editor 与 Networking 类型程序集归属
- Entities Source Generator 与 NetCode Source Generator
- Player `InputCommand` 和 Networking RPC、Ghost Variant 生成
- 全部 `Assets/Scenes` 场景 Missing Script 扫描
- 全部 `Assets/Prefabs` Prefab Missing Script 扫描
- Build Settings 中启用场景的 Windows Client 构建
- Build Settings 中启用场景的 Windows Dedicated Server 构建
- 程序集依赖、命名空间和全局基线审计

最终结果：

- Unity 完整导入通过
- NetCode 和 Entities 代码生成通过
- 阶段四自动验收通过
- Scene 和 Prefab Missing Script 数量为 0
- Windows Client 构建为 `Succeeded`，错误数为 0
- Windows Dedicated Server 构建为 `Succeeded`，错误数为 0
- 审计严重问题和依赖边界违规均为 0

## 8. 后续工作

下一阶段是 Presentation 迁移。

优先处理：

1. 决定 Mono 与 ECS UI 合并为一个 Presentation 程序集还是保留两个单向程序集
2. 消除 Mono 与 UI 之间最后一组双向依赖
3. 迁移菜单、LAN、HUD、音频、GameObject View 和场景过渡
4. 把临时 `Assembly-CSharp` 桥接归入明确的 Presentation Runtime 或 Editor 边界
5. 再次检查全部 Scene 中的 MonoBehaviour 和序列化程序集限定名称
