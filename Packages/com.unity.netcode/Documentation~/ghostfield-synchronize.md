# 使用 `GhostFieldAttribute` 进行序列化与同步

使用 [`GhostFieldAttribute`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html) 指定 [`Unity.Entities.IComponentData`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.IComponentData.html) 或 [`Unity.Entities.IBufferElementData`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.IBufferElementData.html) 中需要序列化并从服务器复制到客户端的字段和属性。当组件或缓冲区中至少有一个字段标记了 `GhostFieldAttribute` 时，系统会自动生成负责组件序列化的结构体

除 `GhostFieldAttribute` 外，还可以使用 [`GhostComponentAttribute`](ghostcomponentattribute.md) 进一步自定义运行时处理复制的方式

<a id="customizing-ghostfieldattribute-serialization"></a>
## 自定义 `GhostFieldAttribute` 序列化

使用以下属性自定义 `GhostFieldAttribute` 对组件和缓冲区的序列化方式。详细信息请参阅 [`GhostFieldAttribute` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html)

| 属性 | 默认值 | 说明 |
|---|---|---|
| [`Quantization`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_Quantization) | float 默认禁用，整数不可用 | 使用 `Quantization` 属性为浮点数及其他受支持类型设置[量化](compression.md#quantization)，以限制数据精度；支持类型请参阅 [Ghost 类型模板](ghost-types-templates.md)。浮点数会乘以量化值并转换为整数，从而节省带宽 |
| [`Composite`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_Composite) | 默认禁用 | 使用 `Composite` 属性控制[增量压缩](compression.md#delta-compression)如何为结构体等非原始字段计算变化位掩码。设为 `true` 时，增量压缩模板只生成一位，用于表示整个结构体内是否存在任何变化。设为 false 时，每个字段都有自己的变化位。如果所有字段通常一起修改，例如 `GUID`，请使用 `Composite=true` |
| [`SendData`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_SendData) | 默认启用 | 使用 `SendData` 属性指示代码生成过程不要把字段包含在序列化数据中。这对结构体等默认序列化全部字段的非原始成员尤其有用 |
| [`Smoothing`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_Smoothing) | 默认为 [`Clamp`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SmoothingAction.html#Unity_NetCode_SmoothingAction_Clamp) | 使用 `Smoothing` 属性控制 Ghost 处于 `GhostMode.Interpolated` 时字段的更新方式。可选值为：`Clamp`，每次收到快照都将客户端值直接设为最新快照值；`Interpolate`，每帧在最近两份快照值之间进行插值，如果下一 Tick 没有数据则停在最新值；`InterpolateAndExtrapolate`，每帧在最近两份快照值之间进行插值，如果下一 Tick 没有数据，则使用前两份快照值线性外推下一值 |
| [`MaxSmoothingDistance`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_MaxSmoothingDistance) |  | 使用 `MaxSmoothingDistance` 属性，在两份快照之间的数值变化超过指定限制时禁用插值，例如可用于处理瞬移 |
| [`SubType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_SubType) |  | 使用 `SubType` 属性，通过 [`GhostFieldSubType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldSubType.html) API 为字段指定自定义序列化器 |

>[!NOTE]
> 同时标记为静态优化和插值模式的 Ghost **永远不会**外推。静态优化 Ghost 未发生变化时不会发送快照更新，因此无法区分“持续变化的值已经停止变化”与“尚未收到下一个持续变化值”

<a id="ghostfield-inheritance"></a>
### `GhostField` 继承

如果为非原始字段类型指定 `[GhostField]`，其特性和部分属性会自动被所有未自行声明 `[GhostField]` 的子字段继承。例如：

```c#
public struct Vector2
{
    public float x;
    [GhostField(Quantization=100)] public float y;
}

public struct MyComponent : IComponentData
{
    // Value.x 会继承父级定义中指定的量化值 1000
    // Value.y 会保留自身原有的量化值 100
    [GhostField(Quantized=1000)] public Vector2 Value;
}
```

> [!NOTE]
> `SubType` 属性始终重置为默认值

<a id="component-serialization"></a>
## 组件序列化

若要将组件标记为需要序列化和复制，请为需要发送的值添加 `[GhostField]` 特性

组件声明必须满足以下要求：

- 必须是具体类型，不支持泛型结构体
- 必须是 `public` 或 `internal`
- 必须实现 `IComponentData` 或任何继承该接口的接口，支持继承 `IComponentData` 的泛型接口

```csharp
public struct MySerializedComponent : IComponentData
{
    [GhostField]public int MyIntField;
    [GhostField(Quantization=1000)]public float MyFloatField;
    [GhostField(Quantization=1000, Smoothing=SmoothingAction.Interpolate)]public float2 Position;
    public float2 NonSerializedField;
    ...
}
```

只有组件的 `public` 成员可以序列化。为 `private` 成员添加 `[GhostField]` 不会产生任何效果

<a id="dynamic-buffer-serialization"></a>
## 动态缓冲区序列化

若要将缓冲区标记为需要序列化和复制，必须为所有 `public` 字段添加 `[GhostField]` 特性

缓冲区声明必须满足以下要求：

- 必须是具体类型，不支持泛型结构体
- 必须是 `public` 或 `internal`
- 必须实现 `IBufferElementData` 或任何继承该接口的接口，支持继承 `IBufferElementData` 的泛型接口

```csharp
public struct SerialisedBuffer : IBufferElementData
{
    [GhostField]public int Field0;
    [GhostField(Quantization=1000)]public float Field1;
    [GhostField(Quantization=1000)]public float2 Position;
    public float2 NonSerialisedField; // 这是明确的错误
    private float2 NonSerialisedField; // 允许这样做，但在客户端读取前应自行设置该值
    [GhostField(SendData=false)]public int NotSentAndUninitialised; // 允许这样做，但在客户端读取前应自行设置该值
    ...
}
```

可以使用 [`SendData` 属性](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_SendData)跳过字段的序列化和复制，这意味着：

- 未复制字段的值永远不会被修改
- 对于新的缓冲区元素，其内容不会被设为默认值，而是处于未定义状态，可以是任意值

动态缓冲区字段不支持 [`SmoothingAction`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SmoothingAction.html)，因此缓冲区会忽略 `GhostFieldAttribute.Smoothing` 和 `GhostFieldAttribute.MaxSmoothingDistance` 属性

<a id="icommanddata-and-iinputcomponentdata-serialization"></a>
## `ICommandData` 与 `IInputComponentData` 序列化

可以为输入字段添加 `[GhostField]`，使其从服务器复制到客户端。例如，这可用于在本地计算机上启用其他玩家角色控制器的客户端预测

使用 [`IInputComponentData`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.IInputComponentData.html) 自动同步输入时：

```c#
public struct MyCommand : IInputComponentData
{
    [GhostField] public int Value;
}
```

[`ICommandData`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ICommandData.html) 继承自 [`IBufferElementData`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.IBufferElementData.html)，可以序列化并从服务器复制到客户端。因此，它遵循与[缓冲区](#dynamic-buffer-serialization)相同的规则：如果需要序列化命令缓冲区，就必须标记所有字段

使用 `ICommandData` 时：

```c#
[GhostComponent()]
public struct MyCommand : ICommandData
{
    [GhostField] public NetworkTick Tick {get; set;}
    [GhostField] public int Value;
}
```

命令数据序列化对实现[远程玩家预测](prediction-n4e.md#remote-player-prediction)尤其有用

<a id="adding-serialization-support-for-custom-types"></a>
## 为自定义类型添加序列化支持

可通过 `GhostFieldAttribute` 序列化的类型由模板指定。默认支持的类型列表请参阅 [Ghost 类型模板页面](ghost-types-templates.md#supported-types)

除默认支持的类型外，还可以：

- 为新类型添加自定义模板
- 为某种类型提供自定义序列化模板，并使用 `GhostFieldAttribute` 的 [`SubType` 属性](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_SubType)指定该模板

有关创建模板的详细信息，请参阅[如何使用和编写模板](ghost-types-templates.md#defining-additional-templates)

>[!NOTE]
> 创建序列化模板并不简单。如果可以通过添加 `[GhostField]` 复制某个类型，通常直接这样做会更容易。如果无法访问该类型，可以改为创建[变体](ghost-variants.md)

## 其他资源

- [`GhostFieldAttribute` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html)
- [`Unity.Entities.IComponentData` API 文档](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.IComponentData.html)
- [`Unity.Entities.IBufferElementData` API 文档](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.IBufferElementData.html)
- [Ghost 类型模板](ghost-types-templates.md)
- [Ghost 变体](ghost-variants.md)
- [使用 `GhostComponentAttribute` 自定义复制行为](ghostcomponentattribute.md)
- [预序列化 Ghost](optimization/optimize-ghosts.md#preserialize-ghosts)
