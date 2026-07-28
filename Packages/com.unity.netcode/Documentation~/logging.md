# 日志

Netcode for Entities 内置日志组件，可以调整输出的日志信息量。通用日志消息与 Ghost 快照/数据包日志可以分别控制

<a id="generic-logging-message-and-levels"></a>
## 通用日志消息与级别

日志消息会输出到 Unity 当前使用的标准日志目标；在编辑器中是 Console 或 `Editor.log`。可以通过设置 `NetDebug.LogLevelType` 修改日志级别。可用级别如下：

* Debug
* Notify
* Warning
* Error
* Exception

默认日志级别为 _Notify_，其中包含信息性消息以及警告、错误等更高重要度消息。如果需要查看连接流程和已接收 Ghost 的更多细节，可以选择 _Debug_ 级别，它会提供更详细、适合排查问题的消息

<a id="ghost-snapshot-logging-packet-dumps"></a>
## Ghost 快照日志与数据包转储

还可以启用 Ghost 快照的详细日志，说明快照如何写入通过网络发送的数据包
Ghost 快照日志非常冗长且开销较高，因此应谨慎使用，例如只在排查 Ghost 复制问题时启用

若要启用 Ghost 快照日志，请为需要调试的连接实体添加 `EnablePacketLogging` 组件。每条连接会创建一个文件

例如，若要为建立的**每一条**连接添加该组件，可以在系统中编写：

```c#
[BurstCompile]
public void OnUpdate(ref SystemState state)
{
    state.EntityManager.AddComponent<EnablePacketLogging>(SystemAPI.QueryBuilder().WithAll<NetworkId>().WithNone<EnablePacketLogging>().Build());
}
```

在 Windows、macOS 和 Linux 等桌面平台上，数据包日志转储会写入普通日志文件所在的同一目录
在 Android 或 iOS 等移动平台上，日志会写入应用具有写入权限的持久化文件位置

- Android 上，日志文件输出到 `/Android/data/BUNDLE_IDENTIFIER/files`，需要使用能够查看隐藏文件的文件管理器来获取
- iOS 上，日志文件输出到应用容器的 `/var/mobile/Containers/Data/Application/GUID/Documents`。可以通过 Xcode 的 **Devices and Simulators** 窗口获取：在 **Installed Apps** 列表中选择应用，单击下方三个点，再选择 **Download Container...**

>[!NOTE]
>这些日志文件不会自动删除，需要手动清理，而且可能变得非常大

<a id="packet-logging-debug-defines"></a>
### 数据包日志调试定义

默认情况下，数据包日志可在编辑器和 Development 构建中使用。即使未启用日志，额外的日志代码也可能影响性能，因此 Release 构建默认禁用该功能

可以在编辑器中将 `NETCODE_NDEBUG` 添加到项目设置的 _Scripting Define Symbols_ 字段，强制禁用该功能。若要在 Player 构建中强制禁用，需要将 `NETCODE_NDEBUG` 添加到 _DOTS_ 项目设置的 _Additional Scripting Defines_

>[!NOTE]
> 此定义还用于保护其他调试功能，例如 `PrefabDebugName`、`WarnAboutStaleRpcSystem` 等
> Netcode 的外部浏览器工具 Net Debugger（Unity NetDbg）可以通过 `NETCODE_DEBUG` 启用，但不能通过 `NETCODE_NDEBUG` 禁用
> 通过 `NetCodeDebugConfig` 设置 `NetDebug` 的 `LogLevel`，也完全不会影响 Netcode 使用 `UnityEngine.Debug` 输出的日志

<a id="netcode-debug-info-warning-and-error-logs"></a>
### Netcode 的 Debug、Info、Warning 与 Error 日志

Netcode 包尽量使用 `NetDebug` 输出全部包日志，但也存在例外；由于 `NetDebug` 早期功能限制，部分位置改用了 `UnityEngine.Debug`
两个日志器都可以在编辑器、Development 构建和正式构建中使用，并且除少数例外外，都会忽略 `NETCODE_NDEBUG` 定义
如果安装了可选包 _com.unity.logging_，`NetDebug` 包装器会使用它，并且不会输出调用栈。相关切换参阅 `USING_UNITY_LOGGING` 定义；未使用该包时会回退到 UnityEngine 内置日志

<a id="custom-packet-dump-messages"></a>
### 自定义数据包转储消息

可以使用 `EnablePacketLogging.LogToPacket` 方法向数据包转储写入自定义信息，但需要注意：

- 自定义代码必须位于 `#if NETCODE_DEBUG` 定义内
- 必须以可写方式访问 `EnablePacketLogging` 结构，以保证 Job 安全，并确保 Netcode for Entities 写日志时不会同时写入。日志使用锁，但 Netcode for Entities 会通过多次调用连续写入多行

<a id="simple-ways-of-enabling-packet-logging-and-changing-log-levels"></a>
## 启用数据包日志和修改日志级别的简便方式

可以通过以下任一方式轻松修改日志级别并启用数据包转储：

- 在编辑器进入 Play 模式后使用 [**PlayMode Tools** 窗口](playmode-tool.md)
- 为 SubScene 中的 GameObject 添加 `NetCodeDebugConfigAuthoring` 组件

若要调试特定连接，需要编写用户代码，为对应连接实体添加 `EnablePacketLogging` 组件

<a id="input-ie-command-packet-dumps"></a>
## 输入，也就是命令的数据包转储

数据包转储包含当前正在发送的输入命令信息

客户端 World 转储示例：

```text
[CSS][ShipCommandData:15257441568649283849] Sent for inputTargetTick: 262 | Entity(1205:3) on GhostInst[type:0|id:191,st:56] | isAutoTarget:True
    | stableHash: 64 bits [8 bytes]
    | commandSize: 16 bits [2 bytes]
    | autoCommandTargetGhost: 64 bits [8 bytes]
    | numCommandsToSend(4): 5 bits
    [b]=[355|→-1 ↑-1] (tick: 32 bits [4 bytes]) (data: 8 bits)
    [1]=[354|→-1 ↑-1] (cb: 0) (tΔ: 2 bits)
    [2]=[353|→-1 ↑-1] (cb: 0) (tΔ: 2 bits)
    [3]=[352|→1 ↑-1] (cb: 1) (tΔ: 2 bits) (data: 6 bits)
    | payloadTicks: 38 bits [5 bytes]
    | payload: 10 bits [2 bytes]
    | changeBits: 3 bits
    | flush: 5 bits
    ---
    208 bits [26 bytes]
```

* `[CSS]` 表示 `CommandSendSystem`
* `[ShipCommandData:15257441568649283849]` 表示输入类型的类型名称与哈希
* `Sent for inputTargetTick: 262 | Entity(1205:3) on GhostInst[type:0|id:191,st:56] | isAutoTarget:True` 表示产生该输入的实体详情
* `stableHash` 表示发送输入类型哈希所需的位数
* `commandSize` 表示命令载荷大小
* `autoCommandTargetGhost` 表示发送 Ghost 标识所需的大小
* `numCommandsToSend(4)` 表示发送本载荷所包含命令数量的字段大小；此处包含 4 条命令
* 此处的 `[b]` 表示**基线**输入，也就是客户端最近产生的最新输入
* `[1]` 等表示此数据包内发送的较早输入索引。`[1]` 是基线之前的上一条输入，`[2]` 是再上一条输入，依此类推
* `[355|→-1 ↑-1]` 表示 `InputBufferData<YourInputTypeHere>`，其 Tick 值为 `355`，用户自定义的 `ToFixedString` 重写返回 `→-1 ↑-1`。本例中对应 NetCube 的 `CubeInput`
* `[Invalid|...]` 表示上一 Tick 没有输入条目，通常只会在游戏启动时出现。理论上可以不发送这些 `Invalid` 输入，但 `numCommandsToSend` 具有预期值，剔除它们可能造成误导
* 自己的日志中可能出现 `InputBufferData<>` 前缀，表示 `ToFixedString` 是在 `InputBufferData<T>` 上调用，而不是在底层类型上调用
* 可能看到 `?ICD?` 代替输入数据，表示输入结构没有重写可选的 `ToFixedString` 方法。请确保该重载兼容 Burst
* `(tick: 32 bits [4 bytes])` 表示序列化基线 Tick 的成本。基线 Tick 不压缩发送
* `(data: 8 bits)` 表示输入结构本身的压缩大小。第一条输入相对于 `default(T)` 进行增量压缩，后续输入值相对于各自的前一个值进行增量压缩
* `(cb: 1)` 中的 `cb` 表示 changeBit。值为 1 时，说明该输入与上一条不同。每条输入都会发送一个 changeBit，类似具有 GhostField 的组件使用 `composite=true`。Tick 为 `Invalid` 时发送 `0`，因为这种情况下本来就不会读取该输入
* `(tΔ: 2 bits)` 表示序列化与该输入关联的 Tick 相对于上一输入需要多少位。常见差值为 `-1`、`-2` 或 `-3`，均使用 2 位。差值为 `-4` 或更小时使用 Huffman 编码，并以 `assumedDeltaTick.Subtract(4)` 为基线
* `payloadTicks` 表示发送全部输入结构 Tick 值所用的位数
* `payload` 表示发送全部压缩输入结构所用的位数
* `changeBits` 表示发送全部变化位所用的位数，本质上等于 `numCommandsToSend - 1`
* `flush` 表示调用 `DataStreamWriter.Flush` 时，为对齐到字节边界而浪费的位数
* `--- 208 bits [26 bytes]` 表示最终大小，不包括 UTP 与 UDP 标头

服务器 World 转储示例：

```text
[CRS][3480158943696179440] Received command packet from Entity(623:9) on GhostInst[type:??|id:191,st:56] targeting tick 355:
    | arrivalTick:353
    | margin:2
    [b]=[355|→-1 ↑-1]
    [1]=[354|→-1 ↑-1] (cb:0)
    [2]=[353|→-1 ↑-1] (cb:0)
    [3]=[352|→1 ↑-1] (cb:1) Late!
    ---
    26 bytes
```

* 其他信息参阅上文。可以看出，服务器转储与客户端转储相互对应，但元数据不同
* `[CRS]` 表示 `CommandReceiveSystem`
* `Entity(623:9)` 表示服务器上与该 Ghost 实例对应的实体，因此其值与客户端不同
* `GhostInst[type:??|id:191,st:56]` 表示推定的 `GhostInstance` 结构值。此处类型始终未知，但其他字段应与客户端一致；只有使用 `AutoCommandTarget` 时适用
* `arrivalTick:353` 表示该输入在服务器 Tick 353 抵达
* `margin:2` 表示得益于 `TargetCommandSlack`，该输入提前 2 个 Tick 抵达
* `[3]` 上的 `Late!` 表示此数据包包含的前 3 条历史输入中，最旧的一条，也就是向前第 3 条，抵达太晚而无法处理。对历史输入而言这是正常现象，但最新两条通常不应出现，具体取决于配置

> [!NOTE]
> 这些输入数据大多是冗余的，但发送它们可以正确恢复数据包丢失和抖动。客户端输入丢失会导致明显误预测，进而产生明显校正
