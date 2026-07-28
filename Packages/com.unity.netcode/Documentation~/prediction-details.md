# 预测的边界情况与已知问题

使用客户端预测时，需要注意以下已知边界情况

<a id="interactions-between-predicted-ghosts-using-partial-snapshots"></a>
## 部分快照下预测 Ghost 之间的交互

客户端收到服务器的完整快照时，Netcode 会将预测 Ghost 状态回滚到快照状态，再多次运行 `PredictedSimulationSystemGroup`，使客户端模拟重新推进到当前预测 Tick。全部 Ghost 会一起执行 Tick

但是，收到[部分快照](ghost-snapshots.md#partial-snapshots)时，只有快照中包含的 Ghost 会回滚并执行 Tick。例如，快照包含 Ghost A 和 B，但不包含 C 和 D，则只有 A 和 B 会回滚到服务器发来的 Tick，也只有 A 和 B 会重新模拟。这同时适用于 Ghost 数据和输入

**注意事项**：

- 回滚不会无限向前追溯。Netcode 会将回滚范围限制在输入队列大小以内，目前该大小为常量 64
- 部分快照对插值 Ghost 的影响不那么明显。插值 Ghost 会按 `ClientTickRate.InterpolationTimeMs` 定义的毫秒数，或 `ClientTickRate.InterpolationTimeNetTicks` 定义的帧数排队并缓冲，以抵抗抖动。因此，如果 C 和 D 使用插值模式，它们很可能在客户端同时更新，因为 C 的值会被缓冲区延迟，为 D 的值留出抵达时间
- 销毁行为不同。无论 Ghost 优先级如何，Ghost 销毁都会立即发送。除非同一时刻销毁数量很多，大约 100 个，否则同一 Tick 的销毁事件通常会一起发送。在 Asteroids 示例中，子弹与小行星在服务器同一 Tick 销毁，也很可能在客户端一起应用

可以将 `GhostSendSystemData.MaxSendChunks=1` 单例设置为 1，人为测试部分快照；这会强制每次只发送一个 Chunk

[预测生成问题中有类似的说明图](#predicted-spawn-interactions-with-other-predicted-ghosts)

<a id="example"></a>
### 示例

客户端时间线如下，全部过程发生在同一帧内：

- A、B、C 和 D 都已经预测到 Tick 20
- 客户端收到 A 和 B 在 Tick 10 的部分快照
- 客户端将 A 和 B 重置到快照 10 的值，但 C 和 D 保持 Tick 20 的值
- 客户端只为 A 和 B 重放 Tick 11、12、13、14……20
  - C 和 D 仍冻结在 Tick 20
- 客户端为 A、B、C 和 D 全部模拟 Tick 21

这意味着，如果被回滚的 Ghost B 与未回滚的 Ghost C 交互，交互结果可能不正确，因为两者没有以相同速率和相同步数执行模拟。修改一个 Ghost 的状态并期望它使用该状态自行更新时，问题尤其明显。例如，B 与 C 碰撞并修改 C 的速度时，C 仍只会用该速度执行一个 Tick；而 A 和 B 从快照重放到当前 Tick 时，一直与冻结的 C 交互。模拟可能预期 C 使用新速度移动，但实际不会如此

<a id="possible-mitigations"></a>
### 可行的缓解方法

- 使用[客户端预期](https://docs-multiplayer.unity3d.com/netcode/current/learn/dealing-with-latency/#action-anticipation)代替预测
  - 客户端不立即开始操作，而是等待服务器确认后执行，同时播放动画或音效掩盖延迟。例如，如果上例中的玩家是 Ghost A、待拾取的球是 Ghost C，那么球的状态较晚抵达时，预测拾球可能需要意外长的时间才能校正
    - 这只是示例。实际项目中，如果正确设置优先级，玩家附近的球很可能很快被接收，甚至已经包含在当前部分快照内
- 主动修改其他 Ghost 的状态，而不是等待它们自行修改
  - 例如，Ghost A 拾取 Ghost C 时，不要让 C 根据 A 自行更新位置，而由 A 直接更新 C 的位置
  - 这样，当前正在模拟并带有 `Simulate` 标签的 Ghost 会在受其影响的 Ghost 上产生预期结果，即使后者自身没有执行模拟
- 使用 `GhostGroup`
  - `GhostGroup` 保证组内 Ghost 一起发送，因此可以消除部分快照问题。不过，该方案更复杂，需要谨慎分组
- 提高 Chunk 优先级
  - 让球附近的 Ghost 获得更高优先级，并使球始终具有最高优先级，每个 Tick 都发送。详细信息请参阅[重要度缩放](optimizations.md#importance-scaling)
- 只允许正在模拟的 Ghost 之间交互
  - 在实体查询中使用 `Simulate` 标签筛选可交互实体
  - 这样 A 和 B 只能互相交互，并忽略 C 和 D。该方法仍会产生误预测。与冻结 Ghost 交互和完全跳过它相比，哪种校正更明显取决于具体玩法

总之，此类情况下无法完全避免误预测。目标是尽量掩盖校正，避免明显影响玩家体验，同时确保状态最终收敛到正确结果

<a id="predicted-spawn-interactions-with-other-predicted-ghosts"></a>
## 预测生成 Ghost 与其他预测 Ghost 的交互

如上所述，预测生成 Ghost 尚无快照，因此不会回滚和重新模拟，并会遇到与预测 Ghost 类似的问题

### 示例

- 客户端有一个“投球器”，会生成球并使其沿弧线飞行
- 客户端已经生成并收到球 A 的快照，球 A 当前在生成点附近飞行

![预测回滚偏差 1](images/PredictionRollbackDiscrepancy1.jpg)

- 客户端随后预测生成球 B，球 B 执行一个 Tick 并向前飞行一小段距离

![预测回滚偏差 2](images/PredictionRollbackDiscrepancy2.jpg)

- 客户端收到包含球 A 状态的快照，其中旧位置位于球 B 后方
- 客户端将球 A 回滚到快照位置，**但不会回滚球 B**
- 客户端重放多个模拟 Tick。球 A 向前移动，却与冻结的球 B 碰撞；球 B 尚无快照，因此不参与重新模拟
  - 这会持续产生明显的误预测

![预测回滚偏差 3](images/PredictionRollbackDiscrepancy3.jpg)

- 球 B 收到第一份快照后，误预测才会得到校正

![预测回滚偏差 4](images/PredictionRollbackDiscrepancy4.jpg)

### 可行的缓解方法

可以启用[允许预测生成 Ghost 回滚到生成 Tick](ghost-spawning.md#specify-specific-rollback-options-for-predicted-spawned-ghosts)选项来缓解这些问题。启用后，从服务器收到其他预测 Ghost 的快照更新时，系统会恢复预测生成 Ghost 的状态，并从其生成 Tick 开始重新预测

继续使用投球器示例：客户端收到球 A 的状态快照，且球 A 的旧位置位于球 B 后方时，客户端也会将球 B 回滚到其生成时的原始位置

客户端会从最旧的快照 Tick，也就是球 A 的状态更新开始重放多个 Tick。当当前模拟 Tick 到达球 B 在客户端的生成 Tick 时，球 A 与球 B 会一起参与模拟，从而减轻部分误预测问题

<a id="quantization-and-determinism"></a>
## 量化与确定性

客户端和服务器模拟不会在 Tick 之间对值进行[量化](compression.md#quantization)。因此，如果客户端重新模拟 Tick 10、11 和 12，从 10 到 11、从 11 到 12 的过程不会像从快照开始时那样量化初始值。客户端以已经量化的快照状态作为模拟起点，其精度低于服务器对应值，本质上就是不同的值。因此，启用量化会加剧非确定性；量化因子越低、精度越差，两个模拟之间的偏差通常越大

### 示例

- 服务器模拟 Tick 10，float `FOO` 的值变为 `123.12345678`
- 量化设为 `100`，因此发送给客户端的值为 `12312`
- 数据发送期间，服务器模拟 Tick 11 并将该值增加 `0.002`，服务器状态变为 `123.12545678`
- 客户端位于 Tick 20，并收到 Tick 10 的快照
- 客户端回滚到 Tick 10，将 `FOO` 设为 `123.12`，随后开始重新模拟到 Tick 20
  - 模拟 Tick 11 时，客户端同样增加 `0.002`，结果为 `123.122`，由此产生偏差
- 服务器发送 Tick 11 时，量化过程发生舍入，发送的是 123.1**3**
- 客户端收到 Tick 11 后，其模拟值 `123.122` 与服务器值 `123.13` 之间产生更大偏差

解决方法包括：增大量化因子以提高精度；完全禁用量化，但会增加带宽；或者在每个 Tick 结束时自行量化数值，服务器也同样执行，以确保客户端与服务器模拟使用完全相同的基础状态

输入从客户端发送到服务器时不会量化，但如果使用 `[GhostField]` 将输入复制给其他客户端，则会进行量化

总体而言，即使使用未量化值，Netcode 也不保证确定性。Netcode 从根本上不是确定性网络包

<a id="race-condition-and-issue-when-removing-replicated-components-from-predicted-ghost-on-the-client"></a>
## 在客户端从预测 Ghost 移除复制组件时的竞态条件

从预测 Ghost 移除**复制组件**后，系统从最近完整 Tick 的历史备份恢复状态时可能出现问题

预测 Ghost 时，由于存在[部分 Tick](intro-to-prediction.md#partial-ticks)，系统会备份全部预测 Ghost 在最近一个“完整”Tick 的状态。该预测备份包含当时全部组件和缓冲区的当前值，用于继续预测

根据组件移除和重新添加的时机，以及从服务器收到新数据的时机，可能出现不同结果

例如，假设 Ghost 具有 `组件 A`，其中 int 字段初始值为 100，并且每个 Tick 增加 1：

```text
Tick 100 -> A: 100
Tick 101.7 -> A: 101 // 从历史备份取回数据并加 1
Tick 101 -> A: 101  // 完整 Tick，备份 A:101
Tick 102.1 -> 移除 A
Tick 102.2

// 在产生另一份备份前重新添加 A，其值恢复为 101
Tick 102.3 -> 重新添加 A
Tick 102.4 -> A:101，取最近备份值并增加到 102
Tick 102.6 -> 移除 A

// 从服务器收到另一个预测实体 X 在 Tick 99 的快照，A 不受直接更新
Tick 100 -> X
Tick 101 -> X，为 Tick 101 生成新的完整备份，A:0，因为此时组件不存在
Tick 102.8 -> 重新添加 A
Tick 102.9 -> A:0 增加到 1  // 错误，或至少不符合预期

// 从服务器收到另一个预测实体在 Tick 100 的快照，其中 A:100
Tick 101 -> A:101
Tick 102 -> A:102 // 现在重新与服务器同步
Tick 103.2 -> A:103

Tick 104.4 -> 移除 A，移除前 A 为 104
// 收到 Tick 101 的快照，其中 A:101
Tick 102 -> A 不存在，因此不复制或更新值
Tick 103 -> A 不存在，因此不复制或更新值
Tick 104.6 -> 重新添加 A
Tick 104.8 -> A:1
```

最终影响如下：

- 如果在下一次快照备份前移除复制组件，便无法恢复组件当前状态
  - 在重新添加组件并收到新快照前，实体不会应用服务器此前的旧数据或之后收到的更新数据
  - 如果收到包含预测 Ghost 的新快照并触发回滚，新历史备份会把全部已移除组件的数据保存为默认值，而不是服务器最近发送的值；后者在该时刻同样可能不正确

重新添加复制组件时，根据时机不同，从备份恢复的组件值可能是：

- 客户端最近计算的值，前提是在执行新备份前重新添加组件
- 组件的默认全零值，前提是执行备份时组件不存在

无论哪种情况，重新添加组件本身都不会触发回滚和重新模拟，因此实体只能恢复到部分正确的状态

<a id="mitigation"></a>
### 缓解方法

最好的建议是尽量避免或减少在 Ghost 上移除和重新添加复制组件的需求

如果必须采用这种行为，优先在 `GhostUpdateSystem` 更新前移除或重新添加组件，至少可以减少收到服务器新数据时的异常情况

可以强制 Ghost 回滚到收到的最旧快照并重新模拟，以缓解从备份恢复错误数据以及后续状态延续的问题。由于其他 Ghost 和数据不会一同回滚，该方法不能保证计算值完全一致，但能更好地保留 Ghost 状态
