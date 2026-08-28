# 已知边界与演进方向

[返回架构总览](README.md)

本文记录当前实现中已经确认的边界，用于 Code Review、测试和排期。它描述的是现状，不表示这些问题已经在本次文档任务中修复。

## 1. 优先处理的权限问题

下面几项直接影响服务端权威性，应优先于一般重构处理。

1. `ServerStartMatchSystem` 会接受任意连接发来的 `StartMatchRequestRpc`，当前没有 Host 或 Admin 身份校验。服务端应从 `SourceConnection` 验证发起者是否有权开始对局
2. 近战和远程命中 RPC 没有验证发送连接是否拥有攻击者。应检查攻击者的 `GhostOwner.NetworkId` 是否等于发送者
3. 远程命中没有强制使用 `AniPendingAttack.Target`，也没有完整复核距离和视线。最终目标和射线都应由服务器冻结或重算
4. `DebugAdjustResourceRpc` 直接信任客户端提交的正负数量，而且正式 UI 正在使用这条调试链路。它应被替换为语义明确的服务端经济事务
5. 资源扣除和 `SpawnAniRequestRpc` 是两条分离链路。服务端目前没有原子地完成成本检查、扣费、数量上限检查和生成
6. `ServerReceiveAniCommandRpcSystem` 只检查部分目标是否存在，没有按 `TargetKind` 完整验证 Ani、Base、Player 和 Resource 所需的 Tag/Component。伪造目标可能导致错误组件读取
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
- **比赛时间只对 Host 可读**：`ResourceStateReader.TryGetGlobalGameResourceState` 直接查询 Server World，纯 Client 进程没有这个 World
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

- Legacy Planner、Nav Planner 和 Follow 没有完整声明显式先后关系，部分系统可能读取上一帧的目标或路径；Grid 后端的 Squad 更新链路已经使用 System Group、`UpdateAfter` 与 `UpdateBefore` 固定顺序
- Hit RPC Apply 与 `ServerApplyDamageSystem` 没有统一排序，伤害可能延后一帧，具体结算时机也不容易推断
- Planner 高频更新导航请求版本，`ServerNavMeshPlannerSystem` 又在主线程同步调用 `NavMesh.CalculatePath`，大量 Ani 同时移动时成本较高
- `ServerAniAttackSenseSystem` 每帧复制候选数组并执行近似 O(Ani x Target) 的扫描，目标数量增长时成本会快速上升
- `ServerAniGhostIdIndexSystem` 已取消每 Tick 重建，但 Ani 出生、销毁或身份变化时仍会重新发布完整排序索引；高频增减场景需要在后续性能样本中确认尖峰
- 6A.2 已移除正式 Grid 入口的双 Buffer 和严格阵型适配，但每个 Cohort 仍持有自己的 Corridor 与 Flow Field Buffer；相同路线的数据归并和全局 Handle 所有权留待 6A.3
- FSM Blackboard 使用较宽的 `FsmVar` Buffer 和线性查找，Key 越多，CPU 与潜在网络成本越高
- `FsmRegistry` 使用跨 World 的 static Persistent NativeArray，World 销毁时没有完整复位初始化状态，重建或多 World 生命周期可能访问已释放容器
- `ServerAniDeathSystem` 查询所有 `Health`，只排除脆弱资源和大基地。未来玩家或其他实体增加 Health 后可能被误删
- `AniMovementFsmBaker` 创建 Blob 后没有注册到 Baking Blob Store
- Picker 可以在感知系统中选择基地，但基地只带远程可攻击标记，结果可能是反复播放近战攻击却无法结算

Legacy 的 `NavStop` 提前返回和逐 Ani MoveTo 日志已在 `NormalizedLegacy-v1` 中修复，不再列为当前缺陷。该版本只用于建立可比较基线，不代表 Legacy 已完成性能治理。

## 5. 玩家、相机与物理

- Fixed 和 Orbit 两个命令构建系统没有互斥条件。它们会竞争写同一 Tick，`AddCommandData` 的后写结果覆盖前写；当前启用 Fixed Camera 时，Orbit Builder 仍可能用单位相机基覆盖移动方向
- Fixed Camera 的 Damping、SmoothState 和 Snap 参数没有完整参与运行，Inspector 展示的配置与实际行为不完全一致
- Orbit 的部分系统和 `MainCameraSystem` 缺少明确 Client World Filter，多 World 环境可能创建非预期实例
- `EntityViewFollower` 当前没有使用 `PreferLocalToWorld`，这个序列化选项没有实际效果
- Jump 配置、输入和预测移动尚未形成完整闭环
- 部分玩家和点击逻辑使用 `UnityEngine.Physics`，Ani 使用 Unity Physics ECS，Client 与 Server 的碰撞数据边界不统一
- `ClientWorldCommandRaycastSystem` 没有完整防御 Bootstrap 缺失；部分命中没有对应 Proxy 时，还可能沿用上一次目标 Entity

## 6. 表现桥与 static 状态

static 桥接使用方便，但它的生命周期是进程级，不会自然跟随 Scene 或 World 清理。

- 攻击动画命中队列是 static，当前没有明确的 World/Session 清理入口，场景重建后可能残留事件
- `NetworkPresentationEvents` 和各本地事件入口保存 static UnityEvent，监听解除遗漏会产生重复回调和跨场景引用
- Host 同时运行 Client 与 Server World，部分 static 桥没有记录来源 World，两侧事件可能进入同一个进程级队列
- `ClientSpawnHealthBarViewSystem` 使用 static HUD Root 缓存，场景重载依赖 Unity 的 null 语义重新查找
- UI 每帧直接查询 World 状态，World 销毁或切换期间需要完整的失败处理

更稳定的方向是让桥接对象绑定明确的 World/Session，在创建和销毁时成对注册与注销。

## 7. 已存在但尚未形成闭环的设计

以下类型已经出现在代码或烘焙流程中，但不应被默认视为现役架构契约：

- `PlayerCamp` 只有类型声明，现役客户端阵营快照使用 `LocalPlayerCamp`
- `CapsuleColliderGeometry` 会由 Baker 创建，但没有运行时消费者
- `BaseWorldAABB` 会被烘焙，`DistanceUtility` 也已定义，但当前主链路没有调用方
- `AniPhysicsProbe` 会采样地面和障碍，移动、FSM 和表现却没有消费这些结果
- `AvatarAnimationParameters` 只有组件声明，没有接入 Authoring 或动画更新链路
- `FixedCameraSmoothState` 会由 Authoring 创建，当前相机系统没有读取
- `NetworkCameraMath` 已定义但没有调用方
- `AniFormationLeaveRequest` 有消费者，但当前没有生产入口

继续使用这些类型前，应先补齐生产者、消费者、生命周期和测试；确认不再需要后再删除，避免长期保留半成品契约。

## 8. 工程结构

- 自有业务代码已全部进入项目 asmdef，当前风险转为新增依赖是否持续遵守单向边界，以及 `Auto Referenced` 是否被无理由重新开启
- 当前没有独立的 `Assets/Tests`。Navigation 已有专用 Validation 程序集和 Stage 1～5/R6 夹具，但权限校验、FSM、资源事务和开局链路仍缺少自动回归保护
- `AssemblyMigrationStageSevenValidation` 仍固定登记迁移完成时的 13 个程序集，没有包含后来增加的 `AnimarsCatcher.Navigation.Benchmark` 和 `AnimarsCatcher.Navigation.Validation`；静态审计已覆盖当前 15 个程序集，但旧 Unity 总入口在修正前会因数量断言失败
- 旧 Scene 仍在 Unity 项目内，应确认用途和引用后归档或删除
- Build Settings 同时列入主场景和 SubScene，需要确认 SubScene 是否真的需要作为独立 Player 入口
- `Assets/SO` 已用于 Navigation Grid 烘焙资产；其他静态配置仍需继续区分资源配置与运行时状态

Navigation 当前还存在明确的功能边界：

- 阶段五已通过算法和自动夹具，但窄门、连续窄道、动态障碍重新规划及场景性能退出条件仍需正式场景验收
- 阶段六已完成 6A.0～6A.4，包括规模 Harness、MovementOrder、正式 Cohort、连通目标区域、自由 Flow 移动、共享 Field Store、预算调度、并行单位移动和动态目标专项验收；空间哈希、ORCA、选择性 Capsule Cast/Slide、受阻恢复及含避碰与碰撞的万人完整导航性能门禁仍未实现
- 6A.4 的 10000 Ani 完整自由移动回放已全部到达且零主线程托管分配，但 Server Tick P95 为 `3016.4815 ms`、请求排队等待 P95 为 57 Tick，明显未达到阶段六冻结预算；938 次唯一 Field 构建和精确起点 Cell Key 的低复用是 6C 前的主要性能风险
- 6A.3 的共享 Key 为保证结果安全仍包含精确投影起点 Cell，因此不同起点即使最终经过同一 Corridor 也不会合并；是否增加 Corridor 预计算或分层 Key 应以 6B～6C 的低复用压力报告决定
- 阶段七的资源搬运迁移、正式 Prefab/Scene 切换和 Legacy 隔离尚未完成，未指定启动参数时仍使用 Legacy 后端
- Navigation 目录已经按职责拆分，但 namespace 仍统一使用 `AnimarsCatcher.Navigation.Grid`；是否迁移为分层 namespace 应使用独立提交

## 9. 推荐演进顺序

1. 先修复 StartMatch、命中、AniCommand、资源和 Spawn RPC 的服务端权限与事务校验
2. 补齐 Dedicated Server 的场景、阵营和连接生命周期
3. 稳定 Ghost Archetype、Damage Buffer、GameResult 和出生组件写入
4. 明确 System 更新顺序，并降低 Nav、Sense 和 GhostId 映射成本
5. 收敛相机模式和物理实现，删除无效或未接线配置
6. 建立独立 Tests asmdef，以及关键 EditMode、PlayMode 和 NetCode 自动测试
7. 更新程序集 Unity 总验收入口，使其覆盖当前 15 个 asmdef
8. 按 16 号执行计划先完成阶段 6A 规模基础，再完成 6B 避碰与世界碰撞、6C 万人验收，最后进入阶段七的资源迁移和正式后端切换
