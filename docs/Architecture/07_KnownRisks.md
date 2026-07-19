# 已知边界与演进方向

[返回架构总览](README.md)

本文记录当前实现中已经确认的边界，用于 Code Review、测试和排期。它描述的是现状，不表示这些问题已经在本次文档任务中修复。

## 1. 优先处理的权限问题

下面几项直接影响服务端权威性，应优先于一般重构处理。

1. `ServerStartGameSystem` 会接受任意连接发来的 `StartGameRpc`，当前没有 Host 或 Admin 身份校验。服务端应从 `SourceConnection` 验证发起者是否有权开始对局
2. 近战和远程命中 RPC 没有验证发送连接是否拥有攻击者。应检查攻击者的 `GhostOwner.NetworkId` 是否等于发送者
3. 远程命中没有强制使用 `AniPendingAttack.Target`，也没有完整复核距离和视线。最终目标和射线都应由服务器冻结或重算
4. `ResourceChangedRpc` 直接信任客户端提交的正负数量，而且正式 UI 正在使用这条调试链路。它应被替换为语义明确的服务端经济事务
5. 资源扣除和 `SpawnAniRpc` 是两条分离链路。服务端目前没有原子地完成成本检查、扣费、数量上限检查和生成
6. `ServerMovementOrderReceiveRpcSystem` 只检查部分目标是否存在，没有按 `TargetKind` 完整验证 Ani、Base、Player 和 Resource 所需的 Tag/Component。伪造目标可能导致错误组件读取
7. 攻击命中时机依赖客户端动画事件，多个客户端都可能为同一 Shot 回报候选，Dedicated Server 自身也没有独立命中时机。更稳妥的方向是由服务器驱动命中窗口，客户端事件只负责表现

## 2. 网络与会话生命周期

当前正式 Host 流程可以运行，但 Dedicated 和重复进入房间的生命周期还没有闭合。

- **命令行角色不统一**：`-server`、`-serverui` 会触发监听，却不参与 `NetworkRuntimeRole` 判定，可能出现“想监听但没有创建 Server World”的组合
- **Dedicated 阵营不可用**：当前策略会把 Dedicated Server 上的所有连接都分到 Beta
- **Dedicated 场景链路缺失**：Client 切换游戏场景时，Dedicated Server 没有独立的权威场景加载流程
- **Ready 条件脆弱**：客户端依赖 Ghost Prefab 名包含 `Robot`，并在稳定后固定等待 3 秒。改名、加载波动或资源数量变化都可能破坏握手
- **开局事件重复**：`MatchStartedEvent` 在收到开局 RPC 和 Ready 完成时各触发一次，监听 UI 可能执行两遍
- **断线清理不完整**：房间返回和连接超时没有明确创建 `NetworkStreamRequestDisconnect`，成员离开、重试和资源回收缺少统一入口
- **World 无重建路径**：返回主菜单会 Dispose Client/Server World，但当前没有看到重新执行 Bootstrap 的入口，第二次创建或加入房间可能失败
- **比赛时间只对 Host 可读**：`GameResourceGetter.TryGlobalGameResourceState` 直接查询 Server World，纯 Client 进程没有这个 World
- **比赛时间不是实际 Ghost**：`GlobalGameResourceState` 虽然带有 Ghost 字段标注，但所在场景 Entity 没有形成真实 Ghost，因此 Client World 不会收到服务器时间

## 3. Entity 与 Ghost 结构

这部分问题的共同特征是组件职责不够集中，或者运行时结构变化与 Ghost Schema 不一致。

- `CharacterSpawnUtility` 和调用方都会写 `GhostOwner`、`Camp`。当前更多是冗余与职责重复，但后续改动很容易出现两边值不一致
- Ani Prefab 已经包含 Picker/Blaster Tag，生成系统又写一次。这不会必然造成异常，但会让能力来源难以判断
- `AniInTeamTag` 被声明为 Ghost Enableable，运行时却存在 Add/Remove。应统一为预烘焙后切换 Enable Bit
- Formation Member/Request 等 Ghost 组件也会动态 Add/Remove。Ghost 序列化 Schema 来自 Prefab，未预烘焙组件不能按普通 Ghost 字段可靠同步
- RoundRobin 出生状态按值传入 Utility，`NextIndex` 的修改可能无法写回调用方
- 命中系统通过 `ECB.AddBuffer<DamageEvent>` 写入已有 Buffer。该 API 会替换旧内容，同一目标在消费前多次命中时可能只保留最后一次伤害
- SubScene 中保留一个当前禁用的 `GameResultRegistry`，同时 `ServerBaseSpawnSystem` 会运行时创建 `GameResult`。现状只有一个结果实体；如果误启用场景注册对象，就会破坏 Singleton 假设

稳定能力组件应尽量预烘焙到 Prefab。高频状态变化优先使用 Enable Bit，事件 Buffer 使用 Append，而不是反复改变 Ghost Archetype。

## 4. System 顺序与性能

当前有些链路只依赖默认调度顺序。实体数量较少时不一定暴露问题，但规模扩大后会同时影响正确性和性能。

- Planner、Nav Planner 和 Follow 没有完整声明显式先后关系，部分系统可能读取上一帧的目标或路径
- Hit RPC Apply 与 `ApplyDamageSystem` 没有统一排序，伤害可能延后一帧，具体结算时机也不容易推断
- Planner 高频更新导航请求版本，`ServerNavMeshPlannerSystem` 又在主线程同步调用 `NavMesh.CalculatePath`，大量 Ani 同时移动时成本较高
- `AniAttackSenseSystem` 每帧复制候选数组并执行近似 O(Ani x Target) 的扫描，目标数量增长时成本会快速上升
- `ServerApplyAniSelectionRpcSystem` 每帧重建 GhostId HashMap，实体规模增大后会形成持续固定成本
- FSM Blackboard 使用较宽的 `FsmVar` Buffer 和线性查找，Key 越多，CPU 与潜在网络成本越高
- `FsmRegistry` 使用跨 World 的 static Persistent NativeArray，World 销毁时没有完整复位初始化状态，重建或多 World 生命周期可能访问已释放容器
- `ServerNavMeshPlannerSystem` 遇到一个 `NavStop` 后直接 `return`，会跳过本帧后续所有 Agent
- `AniDeathSystem` 查询所有 `Health`，只排除脆弱资源和大基地。未来玩家或其他实体增加 Health 后可能被误删
- `AniMovementFsmBaker` 创建 Blob 后没有注册到 Baking Blob Store；Planner 的 MoveTo 分支还会逐 Ani 每帧输出日志
- Picker 可以在感知系统中选择基地，但基地只带远程可攻击标记，结果可能是反复播放近战攻击却无法结算

## 5. 玩家、相机与物理

- Fixed 和 Orbit 两个命令构建系统没有互斥条件。它们会竞争写同一 Tick，`AddCommandData` 的后写结果覆盖前写；当前启用 Fixed Camera 时，Orbit Builder 仍可能用单位相机基覆盖移动方向
- Fixed Camera 的 Damping、SmoothState 和 Snap 参数没有完整参与运行，Inspector 展示的配置与实际行为不完全一致
- Orbit 的部分系统和 `MainCameraSystem` 缺少明确 Client World Filter，多 World 环境可能创建非预期实例
- `AvatarViewFollower` 当前没有使用 `PreferLocalToWorld`，这个序列化选项没有实际效果
- Jump 配置、输入和预测移动尚未形成完整闭环
- 部分玩家和点击逻辑使用 `UnityEngine.Physics`，Ani 使用 Unity Physics ECS，Client 与 Server 的碰撞数据边界不统一
- `MovementClickRaycastSystem` 没有完整防御 Bootstrap 缺失；部分命中没有对应 Proxy 时，还可能沿用上一次目标 Entity

## 6. 表现桥与 static 状态

static 桥接使用方便，但它的生命周期是进程级，不会自然跟随 Scene 或 World 清理。

- 攻击动画命中队列是 static，当前没有明确的 World/Session 清理入口，场景重建后可能残留事件
- `NetworkUIEventBridge` 保存 static UnityEvent，监听解除遗漏会产生重复回调和跨场景引用
- Host 同时运行 Client 与 Server World，部分 static 桥没有记录来源 World，两侧事件可能进入同一个进程级队列
- `SpawnHealthBarViewSystem` 使用 static HUD Root 缓存，场景重载依赖 Unity 的 null 语义重新查找
- UI 每帧直接查询 World 状态，World 销毁或切换期间需要完整的失败处理

更稳定的方向是让桥接对象绑定明确的 World/Session，在创建和销毁时成对注册与注销。

## 7. 已存在但尚未形成闭环的设计

以下类型已经出现在代码或烘焙流程中，但不应被默认视为现役架构契约：

- `PlayerCamp` 只有类型声明，现役客户端阵营快照使用 `LocalPlayerCamp`
- `CapsulePhysicsInfo` 会由 Baker 创建，但没有运行时消费者
- `BaseWorldAABB` 会被烘焙，`DistanceUtility` 也已定义，但当前主链路没有调用方
- `AniPhysicsProbe` 会采样地面和障碍，移动、FSM 和表现却没有消费这些结果
- `AvatarAnimationParameters` 只有组件声明，没有接入 Authoring 或动画更新链路
- `FixedCameraSmoothState` 会由 Authoring 创建，当前相机系统没有读取
- `NetworkCameraMath` 已定义但没有调用方
- `AniFormationLeaveRequest` 有消费者，但当前没有生产入口

继续使用这些类型前，应先补齐生产者、消费者、生命周期和测试；确认不再需要后再删除，避免长期保留半成品契约。

## 8. 工程结构

- 自有业务代码已全部进入项目 asmdef，当前风险转为新增依赖是否持续遵守单向边界，以及 `Auto Referenced` 是否被无理由重新开启
- 当前没有 `Assets/Tests`，权限校验、FSM、资源事务和开局链路都缺少自动回归保护
- 旧 Scene 仍在 Unity 项目内，应确认用途和引用后归档或删除
- Build Settings 同时列入主场景和 SubScene，需要确认 SubScene 是否真的需要作为独立 Player 入口
- `Assets/SO` 已用于 Navigation Grid 烘焙资产；其他静态配置仍需继续区分资源配置与运行时状态

## 9. 推荐演进顺序

1. 先修复 StartGame、命中、MovementOrder、资源和 Spawn RPC 的服务端权限与事务校验
2. 补齐 Dedicated Server 的场景、阵营和连接生命周期
3. 稳定 Ghost Archetype、Damage Buffer、GameResult 和出生组件写入
4. 明确 System 更新顺序，并降低 Nav、Sense 和 GhostId 映射成本
5. 收敛相机模式和物理实现，删除无效或未接线配置
6. 建立独立 Tests asmdef，以及关键 EditMode、PlayMode 和 NetCode 自动测试
