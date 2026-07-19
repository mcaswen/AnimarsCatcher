# Presentation 程序集迁移

[返回架构总览](README.md)

> 状态：阶段五已完成
>
> 实施日期：2026-07-19
>
> 新增程序集：`AnimarsCatcher.Presentation`、`DOTween.Modules`

## 1. 阶段结果

阶段五把原 `Assets/Scripts/MonoBehaviour` 与 `Assets/Scripts/UI` 下的 53 个脚本统一迁入 `AnimarsCatcher.Presentation`。该程序集覆盖菜单、认证、LAN 房间、HUD、Ani 选择、血条、音频、场景过渡、结算界面、GameObject View 和 ECS 到 Mono 的桥接逻辑。

迁移后 Presentation 只依赖以下项目业务程序集：

- `AnimarsCatcher.Gameplay.Contracts`
- `AnimarsCatcher.Gameplay`
- `AnimarsCatcher.Player`
- `AnimarsCatcher.Networking`

Gameplay、Player 和 Networking 不引用 Presentation。迁移前唯一剩余的 Mono 与 UI 双向候选依赖已经消失，当前项目业务程序集之间没有直接双向依赖。

## 2. 为什么合并 Mono 与 UI

原目录名称看起来像两个模块，但真实运行关系不是独立的上下层：

- ECS 选择 System 需要读取 Mono UI Bootstrap 和 UI 事件
- Mono 面板需要选择模式、资源状态和网络生命周期通知
- 血条 System 负责创建 GameObject View
- 网络表现桥在 ECS World 中消费通知，再驱动 Mono 场景对象

这些代码共享客户端表现生命周期，也需要一起访问场景对象。强行拆成 `AnimarsCatcher.Mono` 与 `AnimarsCatcher.UI` 会保留双向引用，或者引入只为绕过编译器的空接口和静态转发层。

阶段五因此采用一个 Presentation 程序集，同时保留原物理目录。`Assets/Scripts/Presentation` 保存中心 asmdef，`MonoBehaviour` 与 `UI` 根目录通过 asmref 汇入该程序集。该结构与 Gameplay 阶段的中心 asmdef 加多目录 asmref 模式一致。

## 3. 命名空间与职责

全部 Presentation 脚本统一使用 `AnimarsCatcher.Presentation` 根命名空间。常用子命名空间按职责划分：

- `Account` 保存本地认证数据和进程内玩家会话
- `Audio` 保存音乐、音效和音频设置表现
- `Global` 保存现有网络 UI 事件和比赛生命周期桥
- `Lan` 保存局域网发现客户端与主机
- `Selection` 保存 Ani 框选数据、RPC、System 和 Bootstrap
- `HealthUI` 保存血条 Authoring、托管 View 和生成 System
- `Resource` 保存表现层资源查询与请求桥
- `UI` 保存菜单、HUD、转场、结算和小地图组件

阶段五没有移动已有 MonoBehaviour 文件，因此 Scene 和 Prefab 继续使用原 `.meta` GUID。命名空间迁移后，Presentation 范围的全局命名空间脚本数量从 28 个降为 0。

## 4. 运行时边界

Presentation 可以读取运行时状态并提交请求，但不拥有服务器权威业务数据。

主要链路保持为：

1. Networking 产生短生命周期 ECS 通知或稳定 RPC 数据
2. `NetworkPresentationBridgeSystem` 在上层消费通知
3. `NetworkUIEventBridge`、面板控制器或加载界面更新 GameObject 表现
4. UI 输入通过 RPC、请求 Component 或 Player 输入锁状态返回运行时层

网络和玩法程序集不会直接查找场景对象、调用具体面板或依赖 UnityEvent。这个方向由程序集审计门禁固定为 `Presentation -> Runtime`。

## 5. DOTween 兼容边界

项目中的 DOTween 核心是预编译 DLL，但 UI 使用的 `DOFade`、`DOAnchorPos` 等扩展方法来自 `Assets/Plugins/Demigiant/DOTween/Modules` 源码。迁移前这些模块编译在预定义 `Assembly-CSharp-firstpass`，自定义 asmdef 不能反向引用它。

阶段五为现有 Modules 源码增加最小 `DOTween.Modules` asmdef：

- 显式引用 `DOTween.dll`
- 显式引用 `UnityEngine.UI`
- 不修改第三方运行时代码
- Presentation 通过 GUID 引用该模块程序集

这项配置只补齐编译边界，不引入新的 Tween 实现，也不改变现有 UI 动画行为。

## 6. 独立程序集暴露的问题

Unity 完整编译发现并修复了三个只在独立程序集下暴露的问题。

两个 Authoring 内部类都命名为 `Baker`。缺少 Hybrid 引用时，编译器会把基类名称解析为正在声明的非泛型嵌套类型。现在 Presentation 显式引用 `Unity.Entities.Hybrid`，并使用 `Unity.Entities.Baker<T>` 明确基类来源。

`SpawnHealthBarViewSystem` 原先在 `RequireForUpdate` 中使用包含托管组件的 `SystemAPI.QueryBuilder`，迁移后触发 Entities Source Generator 内部异常。现在改为普通 `EntityQuery`，查询条件和运行时行为不变，同时避免源生成器处理该托管查询。

血条命名空间最终使用 `AnimarsCatcher.Presentation.HealthUI`，避免与 Gameplay Contracts 中的 `Health` 组件类型产生名称遮蔽。

## 7. 自动验收

阶段五新增 `AssemblyMigrationStageFiveValidation`，验证代表性类型确实位于 `AnimarsCatcher.Presentation`：

- 玩家会话与音频管理
- 网络表现桥和 LAN 主机
- 主菜单控制器
- Ani 选择 RPC
- 血条 GameObject View

验收同时确认 `DOTween.Modules` 已被 Unity 发现，并复用 Gameplay 验收扫描全部 `Assets/Scenes` 与 `Assets/Prefabs`。扫描结果没有 Missing Script。

Unity 物理副本验证结果：

- 完整导入和脚本编译通过
- Entities 与 NetCode Source Generator 通过
- `AnimarsCatcher.Presentation.dll` 和 `DOTween.Modules.dll` 正常生成
- 阶段五自动验收通过
- Windows Client 构建成功
- Windows Dedicated Server 构建成功

批处理验证使用 `-nographics`，因此 Entities Graphics 会输出无图形设备提示。这些提示不是编译错误，也不影响 Missing Script 与 Player 构建结果。菜单动画、HUD 布局和场景过渡的视觉效果仍应在有图形设备的编辑器中按正常手动验收流程检查。

## 8. 审计结果

阶段五完成后：

- 自有脚本为 268 个
- `Assets/Scripts` 下项目程序集定义为 8 个
- 项目 asmref 为 8 个
- Presentation 53 个脚本命名空间覆盖率为 100%
- 全项目剩余全局命名空间脚本为 28 个
- 直接双向依赖为 0
- 程序集依赖边界违规为 0
- 严重审计问题为 0
- 项目总注释率为 17.36%

剩余 10 条审计 Warning 都来自既有 Navigation 或 Networking 的 Runtime 与 Editor 条件编译混合文件，不属于 Presentation 阶段新增问题。

## 9. 后续工作

下一阶段是 Legacy Benchmark 隔离。

优先处理：

1. 为 `Assets/Scripts/Benchmarks/LegacyNavMesh` 统一命名空间
2. 创建 `AnimarsCatcher.Benchmarks.LegacyNavigation`
3. 只允许 Benchmark 引用正式运行时程序集
4. 验证禁用 Benchmark 后正式玩法仍可编译和构建
5. 保留 Legacy 场景作为 Grid 与 NavMesh 的性能对比基线
