# 限制快照大小

限制快照大小，以减少带宽消耗并提升性能

快照具有最小发送大小，确保只有在至少存在一些需要复制的新实体或已销毁实体时才会发送。此外，还可以使用以下方法进一步优化快照大小：

* 使用 [`GhostAuthoringComponent.MaxSendRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringComponent.html#Unity_NetCode_GhostAuthoringComponent_MaxSendRate) 限制每种 Ghost 预制体类型的重发速率。这可以降低总带宽消耗，尤其是在高优先级大型 Ghost 占满快照时。例如，将 `LootItem` Ghost 预制体类型的 `MaxSendRate` 设为 10，可使其最多每十个快照复制一次
    * 请注意，`MaxSendRate` 与重要度不同。`MaxSendRate` 强制限制重发间隔，而重要度用于告知 `GhostSendSystem` 下一份快照应优先处理哪些 Ghost Chunk
* 使用每连接组件 [`NetworkStreamSnapshotTargetSize`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamSnapshotTargetSize.html)，在快照超过指定字节大小（`Value`）时停止向其中序列化实体。可以借此对每个连接的带宽消耗施加软限制。若要全局应用限制，请为 [`GhostSendSystemData.DefaultSnapshotPacketSize`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_DefaultSnapshotPacketSize) 设置非零值
* 使用 [`GhostSendSystemData.MaxSendChunks`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_MaxSendChunks) 限制任意一份快照中可添加的最大 Chunk 数量
* 使用 [`GhostSendSystemData.MaxIterateChunks`](https://docs.unity3d.com/Packages/com.unity.netcode@subfolder?=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_MaxIterateChunks) 限制 `GhostSendSystem` 在查找待复制 Ghost 时遍历并序列化的 Chunk 总数。这对处理大量静态 Ghost 很有用
* 使用 [`GhostSendSystemData.MinSendImportance`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_MinSendImportance) 防止过于频繁地发送某个 Chunk 中的实体。还可以使用 [`GhostSendSystemData.FirstSendImportanceMultiplier`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_FirstSendImportanceMultiplier) 提高包含新实体的 Chunk 优先级，确保无论 `MinSendImportance` 如何设置，它们都能被快速复制
    * 在条件允许时，建议使用 `GhostAuthoringComponent.MaxSendRate`，而不是这一全局设置

> [!NOTE]
> 此处所述优化以 Chunk 为单位应用，并且在 Chunk 内容加入快照后才生效。因此，实际发送值会更高。例如，如果 `MaxSendEntities` 设为 100，但有两个各含 99 个实体的 Chunk，实际会发送 198 个实体

## 减少快照历史大小

默认情况下，Netcode for Entities 会为每个连接与 Ghost Chunk 组合保存最多 32 个快照历史缓冲区条目，该数量由 `GhostSystemConstants.SnapshotHistorySize:32` 定义。后续快照可使用这 32 份已发送快照中最近被确认的一份作为基线，对新的 `GhostField` 值进行[增量压缩](compression.md)。常量值 32 最适合以很高速率发送的 Ghost，例如 60Hz，可提供约 500ms 的历史记录

但是，对于 MMO 规模的游戏，`MaxSendRate` 通常要低得多，此时较小的快照历史大小可能更合适
若要修改此常量，请在 **Project Settings** > **Player** > **Scripting Define Symbols** 中定义以下符号之一：

* `NETCODE_SNAPSHOT_HISTORY_SIZE_16` 在减小静态 Ghost 占用与保证动态 Ghost 确认可用性之间取得了较好平衡。建议用于最高 `GhostPrefabCreation.Config.MaxSendRate` 为 30Hz，或 `ClientServerTickRate.NetworkTickRate` 为 30 的项目
* `NETCODE_SNAPSHOT_HISTORY_SIZE_6` 最适合规模更大的项目，例如包含数百个动态 Ghost、数千个静态 Ghost，并且角色控制器已因网络拥塞或广泛使用 `GhostPrefabCreation.Config.MaxSendRate` 而以明显较低频率发送的项目

> [!NOTE]
> 如果某个 Ghost Chunk 的整个快照历史缓冲区都被“传输中”的快照占满，该 Chunk 可能不会再发送给特定连接。传输中是指包含该 Ghost Chunk、发送时间不足一个往返时间且尚未确认的快照
> 调试时请参阅 `PacketDumpResult_SnapshotHistorySaturated` 方法

## 其他资源

* [Ghost 与快照](../ghost-snapshots.md)
