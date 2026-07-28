# 使用 `GhostComponentAttribute` 自定义复制行为

使用 [`GhostComponentAttribute`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostComponentAttribute.html) 及其属性，自定义运行时处理复制的方式

使用 `GhostComponentAttribute` 之前，必须先用 [`GhostFieldAttribute`](ghostfield-synchronize.md) 标记组件，使其参与序列化和复制

<a id="ghostcomponentattribute-properties"></a>
## `GhostComponentAttribute` 属性

使用以下属性自定义 `GhostComponentAttribute` 对复制行为的修改方式。详细信息请参阅 [`GhostComponentAttribute` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostComponentAttribute.html)

| 属性 | 默认值 | 说明 |
|---|---|---|
| [`OwnerSendType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostComponentAttribute.html#Unity_NetCode_GhostComponentAttribute_OwnerSendType) | `All` | 使用 `OwnerSendType` 属性并通过 [`SendToOwnerType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SendToOwnerType.html) 指定组件要复制到哪组客户端。例如，可以只将输入命令复制给其他玩家，因为本地玩家已经知道自己的输入。请参阅 [`OwnerSendType` 详解](#ownersendtype-details) |
| [`PrefabType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostComponentAttribute.html#Unity_NetCode_GhostComponentAttribute_PrefabType) | `All` | 使用 `PrefabType` 属性并通过 [`GhostPrefabType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostPrefabType.html) 指定组件存在于预制体的哪些版本。例如，可以从服务器 World 中的 Ghost 版本移除渲染相关组件。请参阅 [`PrefabType` 详解](#prefabtype-details) |
| [`SendDataForChildEntity`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostComponentAttribute.html#Unity_NetCode_GhostComponentAttribute_SendDataForChildEntity) | `false` | 使用 `SendDataForChildEntity` 属性指定组件附加到 Ghost 实体的子实体时是否复制该组件。复制 Ghost 子实体的速度明显慢于复制父 Ghost 实体。此属性也适用于 `[GhostEnabledBit]`。请参阅 [`SendDataForChildEntity` 详解](#senddataforchildentity-details) |
| [`SendTypeOptimization`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostComponentAttribute.html#Unity_NetCode_GhostComponentAttribute_SendTypeOptimization) | `AllClients` | 使用 `SendTypeOptimization` 属性并通过 [`GhostSendType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendType.html) 指定 Ghost 处于预测或插值模式时是否复制组件。例如，可以只在实际预测 Ghost 物理时复制 `PhysicsVelocity`。请参阅 [`SendTypeOptimization` 详解](#sendtypeoptimization-details) |

```csharp
[GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.OnlyInterpolatedClients, SendDataForChildEntity=false)]
public struct MyComponent : IComponentData
{
    [GhostField(Quantized=1000)] public float3 Value;
}
```

<a id="ownersendtype-details"></a>
## `OwnerSendType` 详解

若要根据所有权指定组件复制到哪些客户端，请使用 `GhostComponentAttribute` 的 `OwnerSendType` 属性。按照 [`SendToOwnerType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SendToOwnerType.html) 的定义，`OwnerSendType` 可以是以下值之一：

* `None`：不向任何客户端复制组件
* `All`：向所有客户端复制组件
* `SendToOwner`：只向拥有该 Ghost 的客户端复制组件
* `SendToNonOwner`：向拥有该 Ghost 的客户端以外的所有客户端复制组件

<a id="prefabtype-details"></a>
## `PrefabType` 详解

若要指定组件存在于 Ghost 预制体的哪些版本，请使用 `GhostComponentAttribute` 的 `PrefabType` 属性。按照 [`GhostPrefabType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostPrefabType.html) 的定义，`PrefabType` 可以是以下值之一：

* `None`：组件不存在于任何 Ghost 预制体类型
* `All`：组件存在于服务器和所有客户端
* `Server`：组件只存在于服务器
* `Client`：组件只存在于客户端，不受 Ghost 处于预测还是插值模式影响
* `AllPredicted`：组件存在于服务器，并在 Ghost 处于预测模式时存在于客户端
* `PredictedClient`：组件只在 Ghost 处于预测模式时存在于客户端
* `InterpolatedClient`：组件只在 Ghost 处于插值模式时存在于客户端

例如，将 `[GhostComponent(PrefabType=GhostPrefabType.Client)]` 添加到 `RenderMesh` 后，在服务器 World 中实例化的 Ghost 不会包含 `RenderMesh`，而在客户端 World 中实例化时会包含该组件

> [!NOTE]
> 因此，[运行时预测模式切换](prediction-switching.md)可能会随预测模式变化，在运行中的 Ghost 上添加或移除组件

<a id="senddataforchildentity-details"></a>
## `SendDataForChildEntity` 详解

使用 `GhostComponentAttribute` 的 `SendDataForChildEntity` 属性，指定组件附加到 Ghost 实体的子实体时是否复制该组件。序列化和复制子实体的计算成本较高，因此该属性默认为 `false`

<a id="sendtypeoptimization-details"></a>
## `SendTypeOptimization` 详解

若要根据某个 Ghost 在客户端上处于插值还是预测模式，指定组件要复制到哪些客户端，请使用 `GhostComponentAttribute` 的 `SendTypeOptimization` 属性。按照 [`GhostSendType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendType.html) 的定义，`SendTypeOptimization` 可以是以下值之一：

* `DontSend`：不向任何客户端复制组件。Netcode for Entities 不会修改未收到该组件的客户端上的组件
* `AllClients`：向所有客户端复制组件
* `OnlyPredictedClients`：只向正在预测该 Ghost 的客户端复制组件
* `OnlyInterpolatedClients`：只向正在插值该 Ghost 的客户端复制组件

> [!NOTE]
> 设置 `SendTypeOptimization` 和/或 `OwnerSendType` 以指定组件要复制到哪类客户端，不会影响组件是否存在于预制体上，也不会修改未收到该组件的客户端上的组件

## 其他资源

* [`GhostComponentAttribute` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostComponentAttribute.html)
* [使用 `GhostFieldAttribute` 进行序列化与同步](ghostfield-synchronize.md)
* [`SendToOwnerType` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SendToOwnerType.html)
* [`GhostPrefabType` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostPrefabType.html)
* [`GhostSendType` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendType.html)
