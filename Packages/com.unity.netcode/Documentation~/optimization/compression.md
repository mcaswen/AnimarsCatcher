# 数据压缩

使用数据压缩减少带宽消耗，尽量降低玩家因带宽受限而遇到玩法问题的可能性

> [!NOTE]
> 为便于快速运行项目，Netcode for Entities 默认采用带宽消耗较高的快照发送配置。发布正式游戏之前，应修改默认带宽消耗。有关游戏优化方法的详细信息，请参阅[性能优化页面](../optimizations.md)

## 量化

量化是指限制数据精度，从而减少发送和接收该数据所需的位数。一个 float 占用 32 位，按照 IEC 60559 标准，其近似范围为 `±1.5 x 10^−45 到 ±3.4 x 10^38`，精度高于大多数游戏的实际需要。例如，如果不需要毫米级精度，将量化值设为 `100` 会截去 float 中所有低于毫米级的噪声，从而减少发送 float 值所需的位数

量化与客户端预测共同使用时可能引发问题。详细信息请参阅[预测边界情况页面](../prediction-details.md)

### 压缩模型

Netcode 的量化针对后续执行的 [Huffman 增量压缩](https://en.wikipedia.org/wiki/Huffman_coding)进行了优化。这意味着发送较小的值，包括数值之间较小的增量，可以获得最大的带宽收益

例如，以量化值 `10` 发送 `123456789.123456789` 时，Netcode 实际复制的值为 `1234567891`。新 Ghost 生成时，增量压缩会以 0 为基线；由于 Huffman 编码增量 `1234567891` 需要很多位，因此几乎无法产生优化效果。Netcode for Entities 的压缩模型按数值区间进行压缩，数值越小所需位数越少，所以不同大数值之间的压缩效果差异不大，而小数值之间则会有明显差异

因此，以量化值 `10` 发送 `0.123456789` 时，只会发送数值 `1`，Huffman 压缩仅使用 3 位。量化值为 `100` 时使用 7 位，量化值为 `1000` 时使用 13 位，依此类推。可以使用 `StreamCompressionModel.Default.GetCompressedSizeInBits(some_uint_value)` 自行测试。若要测试 `0.123456789` 在量化值 `100` 下的大小，将 0.123456789 乘以 100，转换为 uint 以截去小数部分，再调用 `StreamCompressionModel.Default.GetCompressedSizeInBits(12)`

## 增量压缩

如上所述，对于相同类型，发送的值越小，所需位数越少。如果一个 32 位 float 的变化很小，发送它可能只需不到 8 位。游戏对象通常以较小步长移动，而不是不断瞬移，因此发送每次数值变化的增量，而非每次都发送绝对值，可以显著节省带宽。使用 [`GhostFieldAttribute` 的 `Composite` 属性](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_Composite)自定义组件的增量压缩

请注意，增量压缩以基线为参照进行计算。对于[预生成 Ghost](../ghost-spawning.md#pre-spawned-ghosts)，该基线会以 Ghost 的初始值更新，而不是以零为基线
