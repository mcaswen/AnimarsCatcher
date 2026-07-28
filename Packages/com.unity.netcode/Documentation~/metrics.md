# 指标

有两种方法可以收集 Netcode 模拟指标。最简单直接的方法是在编辑器的 Multiplayer 菜单中使用 Network Debugger，它会提供一个用于查看指标的简单 Web 界面

第二种方法是创建一个 [GhostMetricsMonitor](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostMetricsMonitor.html) 类型的单例，
并填入需要监控的数据点

以下示例创建一个包含全部可用数据指标的单例
将对应的 `IComponentData` 添加到单例，即可启用该指标类型的收集

```csharp
    var typeList = new NativeArray<ComponentType>(8, Allocator.Temp);
    typeList[0] = ComponentType.ReadWrite<GhostMetricsMonitor>();
    typeList[1] = ComponentType.ReadWrite<NetworkMetrics>();
    typeList[2] = ComponentType.ReadWrite<SnapshotMetrics>();
    typeList[3] = ComponentType.ReadWrite<GhostNames>();
    typeList[4] = ComponentType.ReadWrite<GhostMetrics>();
    typeList[5] = ComponentType.ReadWrite<GhostSerializationMetrics>();
    typeList[6] = ComponentType.ReadWrite<PredictionErrorNames>();
    typeList[7] = ComponentType.ReadWrite<PredictionErrorMetrics>();

    var metricSingleton = state.EntityManager.CreateEntity(state.EntityManager.CreateArchetype(typeList));
    FixedString64Bytes singletonName = "MetricsMonitor";
    state.EntityManager.SetName(metricSingleton, singletonName);
```

使用 `SystemAPI.GetSingleton` 访问特定指标类型的数据。例如，访问 `NetworkMetrics`：

```csharp
    var networkMetrics = SystemAPI.GetSingleton<NetworkMetrics>();
```

## 数据点

| 组件类型                    | 说明                                                          |
|-----------------------------|---------------------------------------------------------------|
| `NetworkMetrics`            | 与时间相关的网络指标                                          |
| `SnapshotMetrics`           | 与快照相关的网络指标                                          |
| `GhostMetrics`              | Ghost 相关指标，使用 `GhostNames` 作为索引                    |
| `GhostSerializationMetrics` | Ghost 序列化指标，使用 `GhostNames` 作为索引                  |
| `PredictionErrorMetrics`    | 预测误差，使用 `PredictionErrorNames` 作为索引                |
| `GhostNames`                | 此模拟中全部可用 Ghost 的列表                                 |
| `PredictionErrorNames`      | 此模拟中全部可用预测误差的列表                                |
