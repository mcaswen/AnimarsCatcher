# Gameplay 程序集迁移

[返回架构总览](README.md)

> 状态：阶段三已完成
>
> 实施日期：2026-07-19
>
> 新增程序集：`AnimarsCatcher.Gameplay`

## 1. 阶段结果

阶段三把 Anis、Base、Camp、Global、Health 和 Resource 的正式运行时代码合并到一个 Gameplay 编译边界。

当前没有把六个领域机械拆成六个程序集。它们共享服务端 Simulation 生命周期，并且仍存在合理的同帧数据协作；强行拆分会把更新顺序和状态访问重新变成程序集循环。

Gameplay 通过一个 asmdef 和六个 asmref 组织：

- `Assets/Scripts/Gameplay/AnimarsCatcher.Gameplay.asmdef` 定义程序集和依赖
- Anis、Base、Camp、Global、Health、Resource 各自使用一个 asmref 加入同一程序集
- Anis 下的 Navigation 子目录继续由更具体的 `AnimarsCatcher.Navigation.asmdef` 管理
- Contracts 子目录继续由 `AnimarsCatcher.Gameplay.Contracts.asmdef` 管理

这种结构保留了现有领域目录和 Unity 资源路径，同时让编译器看到一个明确的 Gameplay 边界。asmref 只用于表达同一程序集的多目录组成，不用于隐藏循环依赖。

## 2. Gameplay 边界

Gameplay 当前包含 71 个脚本，统一使用 `AnimarsCatcher.Gameplay` 或其 Editor 子命名空间。

程序集负责：

- Ani 属性、攻击、感知结果、生成和 FSM 运行时
- Base、Camp、Health、Match 和 Resource 的权威规则
- Gameplay Authoring、Baker 和运行时 Component
- Gameplay 内部 System 更新组
- 阶段三场景与 Prefab 序列化验收入口

程序集不负责：

- 菜单、HUD 和结算面板实现
- 网络 World 创建和连接生命周期
- Player 输入采集和 Avatar View 生成
- 旧 NavMesh 移动后端及其资源搬运适配
- Navigation Grid 的烘焙和寻路实现

Gameplay 只允许依赖：

- `AnimarsCatcher.Core`
- `AnimarsCatcher.Gameplay.Contracts`
- 所需 Unity Package 程序集

## 3. 反向依赖清理

阶段二结束时，Gameplay 候选模块仍反向引用 Player、Networking、Mono 和 Legacy Navigation。阶段三按所有权处理这些引用。

### 3.1 输入与生成请求

`MovementClickInputSystem` 移到 Player 输入目录。它读取 `PlayerInput`，再写入 Gameplay 定义的点击请求数据。

`AniSpawnRequestSender` 移到 Mono 桥接目录。它负责寻找 Client World 并创建 `SpawnAniRpc`，不再让 Gameplay 依赖 Networking 的 `WorldManager`。

### 3.2 结算表现

客户端 GameOver RPC 消费、UI Bridge、结果面板和会话返回逻辑移到 Mono Global。Gameplay 只保留服务器胜负判定和权威结果数据。

Ghost Collection 调试系统移到 Netcode Debug，不再作为 Match 领域代码参与 Gameplay 编译。

### 3.3 阵营和初始化顺序

`ServerCampAssignmentPolicy` 不再读取进程级 `NetworkRuntimeRole`，只根据服务器连接编号执行确定性分配。

玩家资源初始化不再通过 `UpdateAfter` 引用 Networking 的具体 System，而是依靠 `NetworkId` 和资源 Ghost Prefab 的数据就绪条件启动。

### 3.4 移动和资源搬运

攻击朝向系统进入 `GameplayPostMovementSystemGroup`。旧物理移动系统从 Legacy 侧声明在该组之前运行，因此 Gameplay 不需要引用 Legacy 的具体 System 类型。

依赖 NavMesh、NavAgent 和旧移动黑板的资源搬运 Setup/Move System 已移入 Legacy Navigation。正式 Resource 领域只保留资源规则、分配请求和权威结算。

### 3.5 资源调试协议

资源调试 RPC 移入 Gameplay Contracts，并统一使用 `ResourceItemKind`。Mono UI 不再维护含义重复的 `ResourceType` 枚举。

## 4. 命名空间与序列化

六个 Gameplay 领域原有的全局命名空间类型已迁入 `AnimarsCatcher.Gameplay`。原脚本和移动脚本的 `.meta` GUID 均保持不变。

`AniAttributes.MoveSpeed` 更名为 `MovementSpeed`，Authoring 字段使用 `FormerlySerializedAs("MoveSpeed")` 保留 Inspector 数据。

程序集和命名空间变化会改变 Ghost、Component 和 MonoBehaviour 的程序集限定名，因此 Client 与 Server 必须使用同一提交重新生成代码和烘焙数据。

## 5. 审计结果

阶段三完成后：

- 自有程序集从 3 个增加到 4 个
- 新增 6 个 asmref，全部指向 Gameplay asmdef 的固定 GUID
- Gameplay 脚本全部具有命名空间
- 候选跨模块依赖从 39 条降到 21 条
- 直接双向依赖从 6 组降到 2 组
- Gameplay 依赖边界违规为 0
- 全局命名空间基线只删除已迁移条目，没有登记新的 Gameplay 例外

剩余双向依赖为：

- Mono 与 Networking
- Mono 与 UI

它们属于后续 Player、Networking 和 Presentation 迁移范围，不进入 Gameplay 或 Contracts。

## 6. 验证范围

阶段三使用完整项目物理副本验证，不使用目录联接或共享源码目录。

验收覆盖：

- Unity Editor 完整导入和脚本编译
- Entities Source Generator 和 NetCode Ghost Serializer
- Gameplay 类型程序集归属
- `Assets/Scenes` 下全部场景的 Missing Script 扫描
- `Assets/Prefabs` 下全部 Prefab 的 Missing Script 扫描
- Build Settings 真实场景的 Windows Client 构建
- Build Settings 真实场景的 Windows Dedicated Server 构建
- 程序集依赖和 asmref GUID 审计
- 项目注释规范检查

最终结果：

- Unity Editor 完整导入和脚本编译通过
- Entities Source Generator 与 NetCode Ghost Serializer 生成通过
- 全部 Scene 和 Prefab 的 Missing Script 数量为 0
- Windows Client BuildReport 为 `Succeeded`，错误数为 0
- Windows Dedicated Server BuildReport 为 `Succeeded`，错误数为 0
- 程序集审计严重问题和依赖边界违规均为 0

## 7. 后续工作

下一阶段是 Player 与 Networking 迁移。

优先处理：

1. 提取 Player 与 Networking 共享的输入、连接和 Spawn 协议
2. 消除 Player 与 Networking 对具体 System 和 Mono Bridge 的引用
3. 把 Client、Server 和 Shared World 职责映射到明确程序集
4. 保持 Gameplay 只被上层消费，不重新引入反向依赖
5. 完成 Host、Client 和 Dedicated Server 的连接及重进流程验证
