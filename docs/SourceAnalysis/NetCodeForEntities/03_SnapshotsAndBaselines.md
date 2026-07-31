# NetCode 快照与基线模型

[返回源码分析目录](../README.md) | [上一篇：时间线与 Tick 模型](02_TimelineAndTickModel.md)

> 分析版本：`com.unity.netcode 1.9.0`
>
> Unity 版本：`6000.2.7f2`
>
> 嵌入源码：`Packages/com.unity.netcode`
>
> 主要阅读范围：`Runtime/Snapshot`、`Runtime/Connection`、`Runtime/Command`
>
> 阅读日期：`2026-07-21`

## 1. 结论先行

NetCode 的 Snapshot 不是“每次把整个世界完整复制给客户端”，而是服务器针对每条连接，从当前相关 Ghost 中挑选一部分状态，用客户端已经确认收到的旧状态作为 Baseline，只发送能够重建当前状态的差异。

这套机制可以概括为：

```text
服务器当前 Ghost 状态
        |
        v
选择客户端已确认的 Baseline
        |
        v
预测当前值并计算 ChangeMask 与字段差异
        |
        v
发送 Snapshot Packet
        |
        v
客户端找到相同 Baseline，解压为完整 Snapshot
        |
        v
写入每个 Ghost 的接收历史
        |
        +--> GhostUpdateSystem 用于插值或预测校正
        |
        v
客户端通过 Command 回传 Snapshot Tick 与 Ack 位图
        |
        v
服务器确认哪些历史版本以后可以作为 Baseline
```

理解源码时最重要的三个结论是：

1. Baseline 是服务器与某个客户端之间的压缩契约，不是全局唯一状态
2. 服务器历史按“连接 + Chunk”保存，客户端历史按“Ghost 实体”保存，两者用途不同
3. 客户端最终写入历史的是解压后的完整 Snapshot，不是网络包里的差异数据

## 2. Snapshot 到底包含什么

服务器发送 Snapshot 的入口是 [`GhostSendSystem`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostSendSystem.cs)，客户端接收和反序列化的核心是 [`GhostReceiveSystem`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostReceiveSystem.cs)。

一个 Snapshot Packet 大致包含：

- 协议类型、往返时间戳、Server Command Age
- `SnapshotSequenceId`
- 当前服务器 `NetworkTick`
- 客户端尚未确认加载的 Ghost Prefab 元数据
- Despawn 信息
- 本次被选中发送的 Ghost 更新

Ghost 更新不是全量世界列表。服务器会根据相关性、Ghost 重要度、最大发送频率、包大小和 Chunk 的上次发送状态决定本次发送哪些内容。因此：

- 一个 Snapshot Tick 可以只包含一部分 Ghost
- 同一个 Ghost 不一定每个 Snapshot 都发送
- 包容量不足时，一个 Chunk 也可能只发送部分实体
- 静态优化 Ghost 在客户端确认稳定状态后，可以长时间不再发送

Snapshot 的 Tick 表示这些状态在服务器时间线上的采样时刻，不表示“这是该 Tick 的完整世界镜像”。

## 3. 两套不能混淆的 Snapshot 历史

### 3.1 服务器：每连接、每 Chunk 的发送历史

服务器使用 [`GhostChunkSerializationState`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostChunkSerializationState.cs) 保存状态。它的职责是记录：

- 服务器曾为这条连接序列化该 Ghost Chunk 的哪些 Snapshot Tick
- 每个历史槽中，当时 Chunk 各索引对应的是哪个 `Entity`
- 每个实体当时序列化后的完整 Snapshot 数据
- 动态 Buffer 当时的长度、ChangeMask 和元素数据
- 每个历史槽是否已经被客户端确认
- 当前环形缓冲写入位置、部分发送起点和静态优化状态

默认历史长度由 `GhostSystemConstants.SnapshotHistorySize` 控制，当前为 32。源码注释给出的设计目标是：在 60 Hz、同一个动态 Chunk 每 Tick 都发送的情况下，约能覆盖 500 ms RTT。

它不是全服务器共享的 Chunk 历史。不同客户端的相关性、丢包、延迟和已确认 Tick 不同，因此每条连接必须维护自己的版本。

内存布局可以简化理解为：

```text
GhostChunkSerializationState
|- Chunk 元数据
|- SnapshotTick[32]
|- AckFlag[32]
|- HistorySlot[0]
|  |- Entity[ChunkCapacity]
|  `- SnapshotBytes[ChunkCapacity]
|- ...
|- HistorySlot[31]
`- DynamicBufferHistory[32]
```

历史槽同时存 `Entity` 很关键。结构变化以后，同一个 Chunk 索引可能已经换成另一个实体。服务器只有在“历史槽中的 Entity 等于当前 Entity”时，才允许把该槽用于这个实体的 Baseline。

### 3.2 客户端：每个 Ghost 的接收历史

客户端使用 [`SnapshotData`](../../../Packages/com.unity.netcode/Runtime/Snapshot/SnapshotData.cs) 和 `SnapshotDataBuffer` 保存每个 Ghost 已经成功解压的 Snapshot，默认同样是 32 个槽。

这套历史供 [`GhostUpdateSystem`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostUpdateSystem.cs) 使用：

- 插值 Ghost 查找目标插值 Tick 前后的两个 Snapshot
- 预测 Ghost 查找新的权威 Snapshot，决定从哪个 Tick 开始恢复和重模拟
- 目标 Tick 超过最新 Snapshot 时，在允许范围内做有限外推
- 时间线向后移动时，尝试使用仍保留在环形缓冲中的更老状态

客户端历史保存的是已经重建完成的完整状态。网络包中的 ChangeMask 和差异数据只用于解码，不能直接供表现层或预测系统读取。

### 3.3 为什么两边都恰好是 32，但不能当成同一套数据

服务器的 32 槽回答的是：

> 我曾经给这个客户端发送过哪些 Chunk 状态，其中哪些已经得到确认，可以拿来压缩下一次发送

客户端的 32 槽回答的是：

> 这个 Ghost 最近成功收到过哪些完整状态，当前插值或预测恢复应该读取哪一个

两者可能出现明显差异：

- 服务器某次 Snapshot 只发送了 Chunk 中一部分实体
- 客户端一帧收到多个 Snapshot 时只保留最新一个待处理包
- 某 Ghost 因优先级较低，很多 Tick 都没有新数据
- 客户端反序列化失败后会废弃 Ack 历史并要求重新同步

因此不能使用客户端 `SnapshotDataBuffer` 去推断服务器一定保留了同样的槽位，也不能把服务器 Chunk 历史理解为客户端回滚存档。

## 4. Ack 如何把两端历史连接起来

Snapshot 使用不可靠传输。服务器不能因为调用过发送接口，就假设客户端已经具备某个 Baseline，必须等客户端明确确认。

每条连接上的 [`NetworkSnapshotAck`](../../../Packages/com.unity.netcode/Runtime/Connection/NetworkSnapshotAck.cs) 负责维护这条确认链路。

### 4.1 客户端记录收到的 Snapshot

[`NetworkStreamReceiveSystem`](../../../Packages/com.unity.netcode/Runtime/Connection/NetworkStreamReceiveSystem.cs) 收到新的 Snapshot Packet 后：

1. 先读取 `SnapshotSequenceId` 和服务器 Tick
2. 丢弃比当前已接收 Tick 更旧的包
3. 把 `ReceivedSnapshotByLocalMask` 按 Tick 差左移
4. 将最低位设置为 1，表示当前 Tick 已收到
5. 更新 `LastReceivedSnapshotByLocal`

如果同一 Unity Frame 内收到多个 Snapshot，接收缓冲只保留最新一个。被后一个包覆盖、没有进入 `GhostReceiveSystem` 处理的包不会继续保持 Ack 位，避免服务器把客户端实际上没有解码和保存的状态选为 Baseline。

客户端保留的回传窗口是最近 32 个 Snapshot Tick：

```text
LastReceivedSnapshotByLocal = Tick 120
ReceivedSnapshotByLocalMask = ...00110101
                                  ||||||||
                                  |||||||`- Tick 120
                                  ||||||`-- Tick 119
                                  |||||`--- Tick 118
                                  `--------- 更早 Tick
```

位为 1 表示收到并保留，位为 0 表示未收到、被覆盖或存在空洞。

### 4.2 Ack 借 Command Packet 返回服务器

[`CommandSendSystem`](../../../Packages/com.unity.netcode/Runtime/Command/CommandSendSystem.cs) 在 Command 头部写入：

```text
LastReceivedSnapshotByLocal.SerializedData
ReceivedSnapshotByLocalMask
```

所以即使玩家当前没有新的业务输入，Command 通道仍承担 Snapshot Ack、时间戳和插值延迟等控制信息的回传。

### 4.3 服务器把 32 位回报扩展成较长历史

服务器收到 Command 后调用 `NetworkSnapshotAck.UpdateReceivedByRemote`。它根据最新 Ack Tick 移动 `ReceivedSnapshotByRemoteMask`，再把客户端回传的 32 位窗口合并到较长的 `UnsafeBitArray` 中。

这个服务器侧 Ack 历史由 `ClientServerTickRate.SnapshotAckMaskCapacity` 控制：

- 默认 4096 bit，即每连接约 0.5 KB
- 最小值 1024 bit
- 60 Hz 下，4096 Tick 约为 68 秒

Ack 历史远大于 32 槽 Chunk 历史，是因为低优先级或静态 Chunk 可能隔很久才再次进入发送队列。服务器再次检查它时，仍需要知道很久以前的 Snapshot Tick 是否曾被客户端确认。

### 4.4 Ack Tick 与 SnapshotSequenceId 不是一回事

Baseline 确认以服务器 `NetworkTick` 为键。`SnapshotSequenceId` 是一个独立的 `byte` 计数器，服务器每次成功派发 Snapshot 后递增，客户端主要用它统计：

- 真正没有到达的包
- 乱序到达后被丢弃的包
- 同一帧到达、被更新包覆盖的包

Snapshot Tick 解决状态时间与 Baseline 查询，Sequence ID 解决包级统计。两者不能互换。

## 5. 服务器如何选择 Baseline

具体逻辑位于 [`GhostChunkSerializer`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostChunkSerializer.cs)。

服务器准备序列化某个 Chunk 时，会从当前写入槽的前一个位置开始，逆序扫描 32 槽历史：

1. 跳过无效 Tick
2. 跳过超过 `MaxBaselineAge` 的历史
3. 使用连接 Ack 位图重新确认该槽是否已被客户端收到
4. 把已确认槽加入 `AvailableBaselines`
5. 统计仍在途、尚未确认的历史数量

`MaxBaselineAge` 当前为 `1 << 28` Tick。它主要用于避免 `NetworkTick` 环形序号比较接近歧义边界，并不是日常网络延迟窗口。

### 5.1 Baseline 是按实体最终确认的

Chunk 历史只是候选集合。真正给某个实体挑 Baseline 时，服务器还会检查历史槽同一索引保存的 `Entity` 是否与当前实体相等。

候选按“最新到最旧”排列，默认选择：

- `baseline0`：最新的可用已确认状态
- `baseline1`：下一个更老的可用已确认状态
- `baseline2`：再下一个更老的可用已确认状态

如果找不到完整的三个匹配状态，源码会同时放弃 `baseline1` 和 `baseline2`，只使用最近的 `baseline0`。

因此正常运行时是以下三种模式，而不是任意数量 Baseline 的组合：

- 零 Baseline：客户端还没有这个运行时 Ghost，按零值发送初始完整状态
- 单 Baseline：对最近一个已确认状态做普通差分
- 三 Baseline：先预测当前状态，再对预测值做差分

为了减少 Baseline Tick 本身的传输开销，连续使用同一组 Baseline 的实体会组成一个 Run。服务器只在 Run 开头写三个 Baseline Tick 差值和实体数量，客户端随后复用这组 Baseline 信息。

### 5.2 为什么不能使用“已发送但未确认”的状态

假设服务器发送了 Tick 100，但该包丢失，随后用 Tick 100 作为 Tick 101 的 Baseline：

```text
服务器：Current(101) - Baseline(100) = 很小的 Delta
客户端：没有 Tick 100，无法还原 Current(101)
```

因此 Baseline 必须来自 Ack 证明客户端确实拥有的状态。使用未确认历史虽然能让 Delta 更小，但会让数据无法解码。

## 6. 三基线预测不是简单线性外推

三基线预测由 [`GhostDeltaPredictor`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostDeltaPredictor.cs) 实现。源码使用三个已确认状态：

```text
baseline2  ->  baseline1  ->  baseline0  ->  currentTick
最旧                              最新       待发送
```

首先根据 Tick 间距计算两个固定点比例：

```text
predictFrac = 16 * (tick0 - tick1) / (tick1 - tick2)
applyFrac   = 16 * (current - tick0) / (tick0 - tick1)
```

然后对每个支持预测的整数或长整数字段执行：

```text
olderDelta        = value1 - value2
predictedValue0   = value1 + olderDelta * predictFrac / 16
recentDelta       = value0 - value1

if abs(value0 - predictedValue0) >= abs(recentDelta)
    predictedCurrent = value0
else
    predictedCurrent = value0 + recentDelta * applyFrac / 16
```

这里包含一个重要的稳定性检查：

- 先用 `baseline2 -> baseline1` 的旧趋势预测 `baseline0`
- 如果这个预测与真实 `baseline0` 的误差已经不小于最近一次真实变化，就认为趋势不稳定
- 趋势不稳定时不继续外推，直接使用最近的 `baseline0`
- 只有旧趋势能较好解释最近变化时，才沿 `baseline1 -> baseline0` 的新趋势预测当前值

例如某个量每 Tick 稳定增加 10：

```text
Tick 97 = 970
Tick 98 = 980
Tick 99 = 990
Tick 100 实际值 = 1000
```

预测值接近 1000，最终只需发送很小甚至为零的差异

如果 Tick 99 突然转向变成 940，旧趋势对 Tick 99 的预测会明显失真，保护分支会退回 940，而不是继续沿旧方向预测 950 或更远的值

源码生成的 Ghost Serializer 在量化后的 Snapshot 字段上调用 `PredictInt` 或 `PredictLong`。预测的目标不是直接改变游戏状态，而是构造一个更接近当前值的临时压缩 Baseline。服务器和客户端使用相同算法，就能得到同一个预测值

## 7. ChangeMask 与字段差分如何配合

服务器序列化一个非 Buffer 组件时，大致执行：

1. 把 ECS 组件复制到当前 Snapshot 表示
2. 如果存在三 Baseline，先把 `baseline0` 的字段预测到当前 Tick
3. 比较当前 Snapshot 与预测后的 Baseline，计算 ChangeMask
4. 只序列化 ChangeMask 标记为变化的 GhostField
5. 对数值继续使用 Packed Delta 编码

客户端执行相反过程：

1. 从自己的 Ghost 历史中找到相同 Tick 的 `baseline0/1/2`
2. 使用相同 `GhostDeltaPredictor` 改写临时 `baseline0`
3. 读取 ChangeMask
4. 以预测后的 Baseline 为默认值，应用收到的字段差异
5. 得到当前 Tick 的完整 Snapshot 并写入历史

因此三基线预测失败不会导致状态错误，只会让最终字段差异变大。真正会导致无法解码的是客户端缺少服务器声明使用的 Baseline Tick

ChangeMask 自身也会相对 Baseline 的 ChangeMask 做 Packed Delta 编码。连续状态稳定时，字段值差异和变化位图都能保持较小

## 8. 动态 Buffer 为什么单独处理

动态 Buffer 的长度、元素数量和内存占用会变化，不能像固定大小组件一样直接放进固定步长的 Snapshot 区域

服务器为每个 Chunk 维护独立的动态历史区，每个历史槽包含：

- 每个实体使用了多少动态数据
- Buffer 长度
- 元素 ChangeMask
- 对齐后的元素 Snapshot 数据

空间不足时，历史区会按 2 的幂扩容，并逐槽搬迁旧数据

客户端同样使用 `SnapshotDynamicDataBuffer` 保存每个 Ghost 的动态历史。收到数据时先根据 Baseline 解出本次动态数据大小，必要时扩容，再写入当前历史槽

三基线字段预测不用于 Buffer 内容。`GhostReceiveSystem` 在执行 `PredictDelta` 时明确跳过 Buffer，包内测试也说明 Buffer 始终使用单 Baseline。原因是 Buffer 的结构变化会让三点趋势预测收益很低，却显著增加 CPU 和内存处理复杂度

## 9. 结构变化如何影响历史

实体发生结构变化后可能移动到另一个 Chunk，或者在同一个 Chunk 中改变索引。旧 Chunk 历史不能直接按新位置使用

默认 `GhostSendSystemData.KeepSnapshotHistoryOnStructuralChange` 为 `true`。服务器会尝试把实体在旧 Chunk 中的历史搬到新 Chunk，但必须满足：

- 能找到旧 Chunk 的序列化状态
- 旧状态仍然有效且 Snapshot 大小和 Chunk Capacity 匹配
- 当前 Ghost 类型不包含动态 Buffer
- 历史槽中的实体身份与目标实体一致

不满足条件时，该实体在新 Chunk 中的历史会被清空，接下来回退到零 Baseline 或较少 Baseline，带宽会短暂增加，但不会使用错误数据

保留结构变化历史是 CPU 与带宽之间的权衡：

- 开启：结构变化时增加查找和复制成本，后续仍可能保持较小 Delta
- 关闭：结构变化处理更简单，但相关 Ghost 需要重新建立可用 Baseline

这个选项不是“保证历史永不丢失”，源码注释明确说明它只能尽力保留

## 10. 包容量、部分发送与历史槽饱和

### 10.1 部分 Chunk 发送

如果剩余包空间不足以容纳整个 Chunk，序列化器可能只写入一部分实体。服务器只把实际发送实体写入当前历史槽，其他索引清为 `Entity.Null`

后续选 Baseline 时，未发送实体无法错误命中这个槽。静态 Chunk 的部分发送会从前部重复发送已经覆盖的实体，因为这些实体一旦有 Ack Baseline，零变化差分通常很小，有利于尽快完成一次完整 Chunk 发送

部分发送不会启用静态零变化跳过。只有完整发送并得到确认后，服务器才能证明客户端具备整个 Chunk 的稳定状态

### 10.2 历史槽饱和

如果网络延迟较高，32 个槽里可能大部分都是“已发送但还没收到 Ack”的在途状态。源码会预留当前写入槽和 Baseline 所需空间，当在途数量达到 `SnapshotHistorySize - 2` 时暂缓再次发送该 Chunk

但如果距离最近一次 Ack 的时间已经超过预估 Snapshot RTT，系统会绕过这个限制，避免一次延迟尖峰永久降低发送频率

这说明缩小 `SnapshotHistorySize` 不是纯内存优化。历史太短会更容易在高 RTT 或高发送频率下失去已确认 Baseline，或者触发历史饱和保护

## 11. 静态优化与 Baseline 的关系

静态 Ghost 并不是“永远只发送一次”。服务器必须先发送一次完整稳定状态，并确认客户端已经 Ack，之后才可以依据 ECS Change Version 跳过该 Chunk

`GhostChunkSerializationState` 记录第一个零变化 Snapshot Tick 和对应 System Version。再次检查静态 Chunk 时：

1. 确认至少一个零变化 Snapshot 已被客户端 Ack
2. 检查相关 GhostField 组件从记录版本以来是否变化
3. 如果都未变化，跳过整个 Chunk
4. 如果发生变化，重新发送并建立新的稳定确认点

所以 Ack 历史太短或客户端重置 Ack，会让静态 Chunk 暂时重新发送。这通常是正确的保守行为：宁可多发一次，也不能假设客户端拥有无法证明的状态

## 12. Spawn、Prespawn 与特殊 Baseline

### 12.1 运行时新 Ghost

客户端第一次接收运行时 Ghost 时没有历史 Baseline。服务器用“Baseline Tick 等于当前 Snapshot Tick”表示零 Baseline，并额外发送 Spawn Tick

客户端从零值开始解码完整初始状态，把结果放入 Spawn 缓冲，实体创建完成后再建立正常的 32 槽历史

### 12.2 Prespawn Ghost

Prespawn Ghost 的初始状态随 SubScene 烘焙，客户端加载场景后理论上已经拥有相同初始数据。因此服务器可以使用 `PrespawnBaseline`，不必重新发送所有初始字段

协议中无效 Baseline Tick 在这里具有特殊含义：使用本地烘焙的 Prespawn Baseline。对于普通运行时 Ghost，无效 Baseline Tick 会被视为协议错误

如果客户端缺少对应 Prespawn Baseline，当前 Snapshot 无法可靠解码，接收系统会报告错误并进入 Ack 重同步

## 13. 客户端如何发现 Baseline 失步并恢复

客户端收到 Baseline Tick 后，会从该 Ghost 的 `SnapshotDataBuffer` 最新槽开始向后查找：

- 单 Baseline 必须找到 `baseline0`
- 三 Baseline 必须同时找到 `baseline0/1/2`
- Prespawn 特殊情况必须找到烘焙 Baseline

如果服务器声明的 Baseline 不在客户端历史中，`GhostReceiveSystem` 会报告 `Ack desync` 并判定整个 Snapshot 反序列化失败

失败后的恢复策略很直接：

1. 客户端把 `ReceivedSnapshotByLocalMask` 清零
2. 客户端把 `LastReceivedSnapshotByLocal` 设为 Invalid
3. 下一次 Command 把“没有有效 Snapshot Ack”回传给服务器
4. 服务器清空该连接的远端 Ack 历史
5. `GhostChunkSerializer.TryAck` 重新检查历史时撤销旧 AckFlag
6. 后续发送回退到客户端能够重新建立的 Baseline，必要时发送零 Baseline 完整状态

这是一次连接级的保守重同步，影响可能大于单个出错 Ghost，但能避免错误 Baseline 持续传播。源码中也保留了 TODO：未来可以只撤销具体出错的 Baseline，而不是清空全部 Ack

## 14. 一条完整链路示例

假设服务器已经给客户端发送并确认了 Tick 97、98、99 的某个移动 Ghost，当前准备发送 Tick 100：

```text
服务器连接历史
Tick 99: 已 Ack，Entity 匹配，位置 9.9
Tick 98: 已 Ack，Entity 匹配，位置 9.8
Tick 97: 已 Ack，Entity 匹配，位置 9.7
```

发送端：

1. 选择 99、98、97 为三个 Baseline
2. 用三点趋势预测 Tick 100 位置约为 10.0
3. 当前真实位置也是 10.0，位置字段 Delta 接近 0
4. 写入 Baseline Tick 差、ChangeMask 和少量变化字段
5. 把 Tick 100 的完整序列化状态写入服务器 Chunk 历史，但先标记为未 Ack

接收端：

1. 在该 Ghost 的接收历史中找到 Tick 99、98、97
2. 使用相同公式预测 10.0
3. 应用包内 Delta，得到权威 Tick 100 完整状态
4. 将 Tick 100 写入客户端 Ghost 历史
5. `GhostUpdateSystem` 在之后的插值或预测恢复中使用它

确认端：

1. 客户端更新本地 Snapshot Ack 位图
2. 下一次 Command 把 Tick 100 及最近 32 Tick 的位图发回服务器
3. 服务器扩展远端 Ack 历史
4. 下次扫描该 Chunk 时，Tick 100 槽成为新的可用 Baseline

如果 Tick 100 包丢失，客户端不会确认它。服务器仍可继续使用 99、98、97，或者等待其他已确认 Snapshot 出现，绝不会仅凭“已经尝试发送”使用 Tick 100

## 15. 对当前项目的实际意义

### 15.1 不要把 Snapshot 频率等同于 Simulation Tick 频率

Snapshot 只是状态采样与发送。降低 `NetworkTickRate` 会减少新历史槽的产生速度和带宽，但不会改变权威玩法每秒执行多少 Simulation Tick

### 15.2 高频变化 Ghost 更依赖三 Baseline

位置、速度、朝向等连续变化且经过量化的字段通常能从三基线预测中获益。大量随机跳变、布尔翻转或离散状态字段预测收益较低

`ForceSingleBaseline` 会减少服务器预测 CPU，但通常增加带宽。它更适合作为分析开关，而不是未经测量直接作为全局优化

建议对单位规模、移动密度和目标 RTT 分别记录：

- 每包大小与每 Ghost bit 数
- `ForceSingleBaseline` 开关前后的服务器 CPU
- Snapshot 丢包和 Ack Desync
- Chunk history saturated 警告

### 15.3 结构变化频繁会破坏压缩连续性

频繁 Add/Remove Component 会让 Ghost 移动 Chunk。即使保留历史开关已启用，也会增加服务器搬迁成本；含动态 Buffer 的 Ghost 还无法走当前历史搬迁路径

需要频繁切换的逻辑状态，优先评估 Enableable Component 或普通字段，而不是反复制造结构变化。最终选择仍需结合 Query 成本、内存布局和业务语义测量

### 15.4 大规模低优先级 Ghost 要关注 Ack 窗口

如果单客户端相关 Ghost 数量极大，同一个 Chunk 可能几十秒才重新发送。此时 4096 Tick Ack 历史也可能不够，表现为静态 Ghost 重复发送或找不到旧 Baseline

优先处理相关性和每连接 Ghost 数量，不应首先盲目增大 Ack Mask。更大的窗口只延后问题，并增加每连接内存

### 15.5 Snapshot 历史不是业务回放系统

客户端的 32 槽历史只服务于短期插值和预测恢复，服务器 Chunk 历史只服务于按连接压缩。两者都不保证保存完整世界，也不适合直接用作录像、断线续传或战斗回放

业务回放需要独立定义采样范围、事件顺序、持久化格式和版本兼容策略

## 16. 排查 Snapshot 与 Baseline 问题的顺序

遇到 Ghost 不更新、带宽异常或 `Ack desync` 时，建议按以下顺序排查：

1. 确认服务器与客户端 Ghost Collection、Prefab Hash 和序列化布局一致
2. 确认报错 Ghost 的 Baseline Tick 是否仍存在于客户端 `SnapshotDataBuffer`
3. 检查客户端是否同帧覆盖了多个 Snapshot，或因乱序丢弃旧包
4. 检查 Command 是否持续发送 Ack，以及服务器 `LastReceivedSnapshotByRemote` 是否推进
5. 检查 Snapshot 丢包、RTT 和 `SnapshotHistorySize` 是否导致大量在途槽
6. 检查 Ghost 是否频繁结构变化、切换 Archetype 或包含大动态 Buffer
7. 检查包目标大小是否长期触发部分 Chunk 发送
8. 检查客户端此前是否发生反序列化失败并清空全部 Ack
9. 使用 NetCode Packet Dump 对照包内 B0、B1、B2、GhostId 和 ChangeMask

不要只根据 Snapshot Tick 连续就判断数据完整。必须同时确认该 Ghost 实际被包含在包中，并成功写入客户端历史

## 17. 建议继续阅读的源码顺序

1. [`GhostSendSystem.cs`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostSendSystem.cs)：Snapshot 头、发送预算、连接级发送入口
2. [`GhostChunkSerializer.cs`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostChunkSerializer.cs)：候选历史、Ack、Baseline 选择、部分发送和静态优化
3. [`GhostChunkSerializationState.cs`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostChunkSerializationState.cs)：服务器 Chunk 历史的内存布局
4. [`GhostDeltaPredictor.cs`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostDeltaPredictor.cs)：三基线预测公式和保护分支
5. [`ComponentSerializationHelper.cs`](../../../Packages/com.unity.netcode/Runtime/SerializationHelpers/ComponentSerializationHelper.cs)：源码生成 Serializer 如何应用预测 Baseline
6. [`NetworkSnapshotAck.cs`](../../../Packages/com.unity.netcode/Runtime/Connection/NetworkSnapshotAck.cs)：Ack 位图扩展、查询和重置
7. [`CommandSendSystem.cs`](../../../Packages/com.unity.netcode/Runtime/Command/CommandSendSystem.cs)：客户端如何回传 Snapshot Ack
8. [`NetworkStreamReceiveSystem.cs`](../../../Packages/com.unity.netcode/Runtime/Connection/NetworkStreamReceiveSystem.cs)：Snapshot 包筛选、同帧覆盖和 Sequence ID 统计
9. [`GhostReceiveSystem.cs`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostReceiveSystem.cs)：Baseline 查找、解压、历史写入与失败恢复
10. [`SnapshotData.cs`](../../../Packages/com.unity.netcode/Runtime/Snapshot/SnapshotData.cs)：客户端环形历史和目标 Tick 查找
11. [`GhostUpdateSystem.cs`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostUpdateSystem.cs)：完整 Snapshot 如何进入插值和预测链路

对应测试建议优先阅读：

- [`SingleBaselineTests.cs`](../../../Packages/com.unity.netcode/Tests/Editor/SerializationTests/SingleBaselineTests.cs)：单 Baseline 与三 Baseline 的正确性和带宽对比入口
- [`SnapshotSequenceIdTests.cs`](../../../Packages/com.unity.netcode/Tests/Editor/SnapshotSequenceIdTests.cs)：丢包、乱序和同帧覆盖统计
- [`PartialSendTests.cs`](../../../Packages/com.unity.netcode/Tests/Editor/PartialSendTests.cs)：小包预算下的部分发送
- [`GhostSerializationTests.cs`](../../../Packages/com.unity.netcode/Tests/Editor/GhostSerializationTests.cs)：历史槽饱和和序列化边界

## 18. 最终认识

NetCode Snapshot 的核心不是“序列化当前值”，而是维护一套可以被两端共同证明的历史关系：

```text
客户端确实拥有某个旧状态
        -> 服务器才允许把它作为 Baseline
        -> 两端使用同一预测与反序列化算法
        -> 客户端得到新的完整状态
        -> 新状态再通过 Ack 成为未来 Baseline
```

Ack 保证正确性，Baseline 和预测降低带宽，ChangeMask 缩小字段范围，环形历史限制内存，结构变化与包预算决定这套压缩关系能否连续保持

对项目而言，优化 Snapshot 不能只盯一个参数。Ghost 相关性、发送频率、字段变化规律、结构变化、包大小、RTT、Ack 窗口和历史长度共同决定最终 CPU、内存与带宽成本
