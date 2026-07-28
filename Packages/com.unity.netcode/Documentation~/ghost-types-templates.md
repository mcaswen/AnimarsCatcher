# Ghost 类型模板

Netcode for Entities 提供默认模板，用于定义 Ghost 组件类型在[烘焙](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/baking-overview.html)和序列化期间的处理方式。还可以[创建自定义模板](#defining-additional-templates)，注册其他类型

<a id="supported-types"></a>

## 支持的类型

Netcode for Entities 默认为以下类型提供序列化模板：

- `bool`
- `Entity`
- `FixedString32Bytes`
- `FixedString64Bytes`
- `FixedString128Bytes`
- `FixedString512Bytes`
- `FixedString4096Bytes`
- `float`
- `float2`
- `float3`
- `float4`
- `byte`
- `sbyte`
- `short`
- `ushort`
- `int`
- `uint`
- `long`
- `ulong`
- 枚举，仅支持 `int` 或 `uint` 底层类型
- `quaternion`
- `double`
- `NetworkEndpoint`
- `FixedList32Bytes<T>`，其中 `T` 可以是任意受支持的非托管复制类型
- `FixedList64Bytes<T>`
- `FixedList128Bytes<T>`
- `FixedList512Bytes<T>`
- `FixedList4096Bytes<T>`
- 固定大小的 Unsafe Buffer，需要为程序集启用 Unsafe Code
- Union，但存在[额外限制](#how-to-support-unions)

<a id="types-that-support-reporting-of-prediction-errors"></a>

### 支持报告预测误差的类型

- `bool`
- `int`
- `uint`
- `short`
- `ushort`
- `long`
- `ulong`
- `byte`
- `sbyte`
- `float`
- `double`
- `float2`
- `float3`
- `float4`
- `quaternion`
- `NetworkTick`
- `NetworkEndpoint`

<a id="types-that-dont-support-reporting-of-prediction-errors"></a>

### 不支持报告预测误差的类型

- `Entity`
- 所有 `FixedString`
- 所有 `FixedList`
- 所有固定 Buffer
- Dynamic Buffer
- Union

<a id="types-with-multiple-templates"></a>

### 拥有多个模板的类型

以下类型提供多种模板，可以选择不同的序列化方式：

- `float`
- `float2`
- `float3`
- `float4`
- `quaternion`
- `double`

这些类型提供以下选项：

| 设置 | 选项 | 说明 |
|---|---|---|
| Quantization | Quantized 或 Unquantized | 量化通过限制数据精度减少收发所需位数。例如，量化因子为 `1000` 时，浮点数 `12.456789` 会作为整数 `12345` 发送。Unquantized 表示以完整精度发送浮点数，详情参阅[量化](compression.md#quantization) |
| Smoothing Method | `Clamp`、`Interpolate` 或 `InterpolateAndExtrapolate` | 平滑方法指定客户端收到快照后如何应用新值，详情参阅 [`SmoothingAction` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SmoothingAction.html) |

每个选项都会改变原始值在客户端上的序列化、反序列化和应用方式。模板使用不同的命名 Region 处理这些情况。代码生成器会选择适当 Region 生成代码，并把用户为类型字段定义的序列化设置直接烘焙到对应序列化器中

可以在项目的 `Temp/NetCodeGenerated` 文件夹查看这些生成类型。Unity 关闭后，该目录会被删除

<a id="fixed-size-list-capacitylength-limitations"></a>

### 固定大小 List 的容量与长度限制

当固定大小 List 作为 RPC、Command 或复制组件的字段进行序列化时，系统会限制其长度和容量

| 数据类型 | 最大元素数 |
|---|---:|
| `IRpcCommand` | 1024 |
| `IComponentData` | 64 |
| `IBufferElementData` | 64 |
| `ICommandData` | 64 |
| `IInputComponentData` | 64 |

固定大小 List 字段通过 RPC 复制时，允许的最大容量，也就是最大长度，为 1024 个元素

> [!NOTE]
> 当前不支持发送大于一个 MTU 的 RPC，因此数据包大小本身还会形成内在限制。实际可序列化元素数可能远低于 1024

固定大小 List 字段通过 `IComponentData`、`IBufferElementData`、`ICommandData` 或 `IInputComponentData` 复制时，最大容量为 64 个元素

固定字节大小隐式决定 `FixedList` 容量，无法直接强制更小容量。因此可以使用字节容量更大的 List，但只允许它保存实际需要的元素数。出现这种需求时，必须使用 [`GhostFixedListCapacity`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFixedListCapacityAttribute.html) 声明预期容量

```csharp
struct MyRpc : IRpcCommand
{
    // 将复制元素数限制为 32，系统会强制执行该限制
    [GhostFixedListCapacity(Capacity = 32)]
    public FixedList4096Bytes<float> Floats;
}

struct MyComponent : IComponentData
{
    // 将复制元素数限制为 32，系统会强制执行该限制
    [GhostFixedListCapacity(Capacity = 32)]
    public FixedList4096Bytes<float> Floats;
}
```

如果字段容量超过上限且没有添加该特性，系统会报告编译错误

序列化或写入快照缓冲区时，如果固定大小 List 的长度超过内部允许上限，会自动截断到最大容量。Development Build 或定义了 `NETDEBUG` 时，List 长度超过上限还会报告错误

<a id="why-we-enforce-the-64-element-restriction"></a>

#### 为什么强制限制为 64 个元素

支持把 `FixedList` 用作 `[GhostField]`，目的是复制少量玩法数据，而不是存储或复制大量数据。元素越多，表示 Change Mask 和 Delta 压缩变更所需的位数就越多

64 个元素是刻意选择的上限。它在灵活性与简单、快速的复制代码之间取得平衡，对大多数用途也足够。它还有助于避免发送部分快照，也就是只包含某个 Chunk 部分实体的快照。未来可能重新评估该限制

针对 Input 或 Command，主要优化目标是让输入通常保持很小，因此包会主动约束其大小。另外，输入也可能通过 `[GhostField]` 以及对应 `GhostComponent` 标志复制给其他玩家；如果不统一限制，这种场景会产生异常且不一致的行为

<a id="why-dont-rpcs-have-a-larger-maximum-allowed-capacity"></a>

#### 为什么 RPC 允许更大的最大容量

主要原因如下：

- RPC 设计为低频发送
- RPC 是最灵活、用途最独立的消息传递工具
- RPC 不需要复杂的 Change Mask 生成，因此没有必要应用相同的 64 元素限制

<a id="how-types-are-serialized"></a>

## 类型的序列化方式

Netcode for Entities 使用位打包的 Packed 格式，或使用完整位宽的 Unpacked 格式，在网络上传输受支持类型

<a id="packed-vs-unpacked-format"></a>

#### Packed 与 Unpacked 格式

| 类型 | Unpacked | Packed |
|---|---|---|
| `sbyte` | 32 bit | ZigZag 编码，4 bit 加 Huffman/Golomb Bucket 可变长度负载 |
| `short` | 16 bit | ZigZag 编码，4 bit 加 Huffman/Golomb Bucket 可变长度负载 |
| `int` | 32 bit | ZigZag 编码，4 bit 加 Huffman/Golomb Bucket 可变长度负载 |
| `long` | 64 bit | 两组“4 bit 加 Huffman/Golomb Bucket 可变长度负载” |
| `byte` | 32 bit | 4 bit 加 Huffman/Golomb Bucket 可变长度负载 |
| `uint` | 32 bit | 4 bit 加 Huffman/Golomb Bucket 可变长度负载 |
| `ushort` | 16 bit | 4 bit 加 Huffman/Golomb Bucket 可变长度负载 |
| `ulong` | 64 bit | 两组“4 bit 加 Huffman/Golomb Bucket 可变长度负载” |
| `float` | 32 bit | 0 使用 1 bit，其他值使用 32 bit |
| `double` | 64 bit | 0 使用 1 bit，其他值使用 64 bit |
| `FixedStringXXX` | 8 bit 加 `length * 8 bit` | 4 bit 加长度的可变负载，再加每个字符的“4 bit 加可变负载” |
| `float2` | `2 * 32 bit` | 两个 Packed Float |
| `float3` | `3 * 32 bit` | 三个 Packed Float |
| `float4` | `4 * 32 bit` | 四个 Packed Float |
| `quaternion` | `4 * 32 bit` | 四个 Packed Float |

<a id="how-to-support-unions"></a>

### 如何支持 Union

`[GhostField]` 会启用两个 Netcode 子系统：序列化，以及客户端预测的备份与恢复

C# Union，也就是组合使用 `[StructLayout(LayoutKind.Explicit)]` 和 `[FieldOffset(0)]`，可以有限度地配合 `[GhostField]` 使用，但有以下限制：

- `SmoothingAction` 必须为 `Clamp`，因为 Netcode 无法推断应使用哪个值，不能执行插值或外推
- `Quantization` 必须为 `0`，也就是关闭
- 预测误差报告不可用
- `[GhostField] Entity` 的复制和修补不可用，例如供 `EntityCommandBuffer` 使用的修补
- 所有 Union 成员共享同一底层内存，只能为尺寸最大的成员启用复制
- `Composite = true` 是可选项
- Delta 压缩在技术上可以工作，但不同状态写入后底层数据发生大幅变化时效率很低

以下示例可用于输入命令、RPC、组件和 Buffer：

```csharp
[StructLayout(LayoutKind.Explicit)]
public struct Union
{
    [FieldOffset(0)]
    [GhostField(SendData = false)]
    public StructA State1;

    [FieldOffset(0)]
    [GhostField(Quantization = 0, Smoothing = SmoothingAction.Clamp, Composite = true)]
    public StructB State2;

    [FieldOffset(0)]
    [GhostField(SendData = false)]
    public StructC State3;

    public struct StructA
    {
        public int A, B;
        public float C;
    }

    public struct StructB
    {
        public ulong A, B, C, D;
    }

    public struct StructC
    {
        public double A, B;
    }

    public static void Assertions()
    {
        UnityEngine.Debug.Assert(
            UnsafeUtility.SizeOf<StructB>() >= UnsafeUtility.SizeOf<StructA>());
        UnityEngine.Debug.Assert(
            UnsafeUtility.SizeOf<StructB>() >= UnsafeUtility.SizeOf<StructC>());
    }
}
```

> Netcode for Entities 不会验证被标记为复制的 Union 成员是否尺寸最大，也不会检查其他成员是否使用了 `SendData = false`，必须自行确保这些条件成立
>
> 建议为 Union 编写[序列化器模板](#defining-additional-templates)，这样可能绕过上述大部分限制

<a id="serialization-in-snapshot"></a>

### 快照中的序列化

复制实体数据，也就是 Ghost Snapshot，由两部分组成：组件字段的 Change Mask 位数组，以及组件数据 Payload 本身

Payload 和 Change Mask 都会相对客户端此前确认的最多三份状态进行 Delta 编码和压缩。如果该实体尚无已确认状态，则相对全零的 Zero Baseline 处理

**Change Mask 位数**

| 类型 | 位数 | 可聚合 | 说明 |
|---|---:|---|---|
| Primitive | 1 bit | 是 | |
| 固定大小 List | 2 bit | 否 | |
| 固定大小 Buffer | 每个元素 1 bit | 是 | |
| `float2` | 1 bit | 是 | |
| `float3` | 1 bit | 是 | |
| `float4` | 1 bit | 是 | |
| `quaternion` | 1 bit | 是 | |
| `FixedStringXXX` | 1 bit | 是 | |

系统会递归访问结构体。默认情况下，每个成员消耗其类型对应的 Change Mask 位数。如果设置 `GhostField.Composite`，结构体内所有支持 Mask 聚合的字段会合并为 1 bit。固定大小 List 始终消耗 2 bit，无法聚合

给定实体的 Change Mask 位保存在整数数组中，并相对客户端最后确认的该实体状态 Change Mask 进行 Delta 压缩

**组件数据**

组件数据始终进行 Delta 压缩，可以相对 Zero Baseline，也可以相对客户端确认的最多三份快照包。因此，所有字段都会使用 `StreamCompressionModel` 以 Packed 格式编码，也就是采用 Huffman/Golomb 压缩

Netcode for Entities 使用预测式 Delta 压缩：根据最多三份 Baseline 预测下一个值，再对字段值与预测结果之间的差值编码

Change Mask 用于明确跳过与当前 Baseline 值相同的字段；Delta 本身按 [Packed 与 Unpacked](#packed-vs-unpacked-format)表中的格式编码

<a id="some-extra-details-about-how-fixed-list-are-serialized"></a>

#### 固定大小 List 的序列化细节

固定大小 List 始终使用 2 bit Change Mask，以及一份动态元素 Mask：

1. 第 1 bit 表示长度相对给定 Baseline 是否变化
2. 第 2 bit 表示是否有任何元素相对给定 Baseline 发生变化

每个 List 元素都会与 Baseline 中相同索引的元素进行 Delta 压缩，并聚合为每个元素 1 bit 的 Change Mask。由于最多包含 64 个元素，序列化时会生成长度可变、最多 64 bit 的元素 Change Mask

如果所有元素都与 Baseline 相同，固定 List Change Mask 的第 2 bit 写入 0，不再发送其他数据。否则第 2 bit 写入 1，并在网络上传输可变长度的元素 Mask 和发生变化的元素数据

<a id="changing-how-a-type-is-serialized-using-variants"></a>

## 使用 Variant 改变类型的序列化方式

可以使用 [`GhostComponentVariationAttribute`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostComponentVariationAttribute.html) 创建 [Variant](ghost-variants.md)，在编译期覆盖默认序列化器。还可以通过 [`GhostAuthoringInspectionComponent`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringInspectionComponent.html)，按 Ghost、按组件应用 Variant

更多信息请参阅[使用 `GhostComponentVariationAttribute` 创建复制 Schema](ghost-variants.md)

<a id="changing-how-a-type-is-serialized-using-the-subtype-property"></a>

### 使用 `SubType` 属性改变序列化方式

同一类型可以定义多个可用模板，例如同时为 `float3` 提供 2D 和 3D 模板。`GhostFieldAttribute` 的 [`SubType` 属性](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html#Unity_NetCode_GhostFieldAttribute_SubType)允许逐个 `[GhostField]` 选择使用哪个模板。详情请参阅[定义 SubType 模板](#defining-subtype-templates)

<a id="defining-additional-templates"></a>

## 定义其他模板

可以创建自定义模板，注册[默认不支持](#supported-types)的其他类型，使它们能通过 `[GhostField]` 正确复制

> [!NOTE]
> 创建序列化模板并不简单。如果某个类型可以直接添加 `[GhostField]` 复制，通常这样做更容易。如果无法修改目标类型，可以改为创建 [Variant](ghost-variants.md)

<a id="writing-a-template"></a>

### 编写模板

模板文件可以放在项目任意包或文件夹中，但必须满足以下要求：

- 文件扩展名必须为 `.NetCodeSourceGenerator.additionalfile`，例如 `MyCustomType.NetCodeSourceGenerator.additionalfile`
- 文件第一行必须是 `#templateid: XXX`，用于为模板分配用户定义的全局唯一 ID

如果创建了没有对应模板文件的 `UserDefinedTemplate`，或创建了没有对应 `UserDefinedTemplate` 的模板文件，都会产生错误。构建模板时的代码生成错误也可能引发编译错误

创建模板时，可以在现有默认模板基础上修改。以下代码复制自默认 `float` 模板，其中 `float` 会量化并保存到 `int` 字段：

```csharp
#templateid: MyCustomNamespace.MyCustomTypeTemplate
#region __GHOST_IMPORTS__
#endregion
namespace Generated
{
    public struct GhostSnapshotData
    {
        struct Snapshot
        {
            #region __GHOST_FIELD__
            public int __GHOST_FIELD_NAME__;
            #endregion
        }

        public void PredictDelta(uint tick, ref GhostSnapshotData baseline1,
            ref GhostSnapshotData baseline2)
        {
            var predictor = new GhostDeltaPredictor(
                tick, this.tick, baseline1.tick, baseline2.tick);
            #region __GHOST_PREDICT__
            snapshot.__GHOST_FIELD_NAME__ = predictor.PredictInt(
                snapshot.__GHOST_FIELD_NAME__, baseline1.__GHOST_FIELD_NAME__,
                baseline2.__GHOST_FIELD_NAME__);
            #endregion
        }

        public void Serialize(int networkId, ref GhostSnapshotData baseline,
            ref DataStreamWriter writer, StreamCompressionModel compressionModel)
        {
            #region __GHOST_WRITE__
            if ((changeMask & (1 << __GHOST_MASK_INDEX__)) != 0)
                writer.WritePackedIntDelta(snapshot.__GHOST_FIELD_NAME__,
                    baseline.__GHOST_FIELD_NAME__, compressionModel);
            #endregion
        }

        public void Deserialize(uint tick, ref GhostSnapshotData baseline,
            ref DataStreamReader reader, StreamCompressionModel compressionModel)
        {
            #region __GHOST_READ__
            if ((changeMask & (1 << __GHOST_MASK_INDEX__)) != 0)
                snapshot.__GHOST_FIELD_NAME__ = reader.ReadPackedIntDelta(
                    baseline.__GHOST_FIELD_NAME__, compressionModel);
            else
                snapshot.__GHOST_FIELD_NAME__ = baseline.__GHOST_FIELD_NAME__;
            #endregion
        }

        public unsafe void CopyToSnapshot(ref Snapshot snapshot,
            ref IComponentData component)
        {
            if (true)
            {
                #region __GHOST_COPY_TO_SNAPSHOT__
                snapshot.__GHOST_FIELD_NAME__ = (int)math.round(
                    component.__GHOST_FIELD_REFERENCE__ * __GHOST_QUANTIZE_SCALE__);
                #endregion
            }
        }

        public unsafe void CopyFromSnapshot(ref Snapshot snapshot,
            ref IComponentData component)
        {
            if (true)
            {
                #region __GHOST_COPY_FROM_SNAPSHOT__
                component.__GHOST_FIELD_REFERENCE__ =
                    snapshotBefore.__GHOST_FIELD_NAME__ * __GHOST_DEQUANTIZE_SCALE__;
                #endregion

                #region __GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_SETUP__
                var __GHOST_FIELD_NAME___Before =
                    snapshotBefore.__GHOST_FIELD_NAME__ * __GHOST_DEQUANTIZE_SCALE__;
                var __GHOST_FIELD_NAME___After =
                    snapshotAfter.__GHOST_FIELD_NAME__ * __GHOST_DEQUANTIZE_SCALE__;
                #endregion
                #region __GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_DISTSQ__
                var __GHOST_FIELD_NAME___DistSq = math.distancesq(
                    __GHOST_FIELD_NAME___Before, __GHOST_FIELD_NAME___After);
                #endregion
                #region __GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE__
                component.__GHOST_FIELD_REFERENCE__ = math.lerp(
                    __GHOST_FIELD_NAME___Before, __GHOST_FIELD_NAME___After,
                    snapshotInterpolationFactor);
                #endregion
            }
        }

        public unsafe void RestoreFromBackup(ref IComponentData component,
            in IComponentData backup)
        {
            #region __GHOST_RESTORE_FROM_BACKUP__
            component.__GHOST_FIELD_REFERENCE__ = backup.__GHOST_FIELD_REFERENCE__;
            #endregion
        }

        public void CalculateChangeMask(ref Snapshot snapshot,
            ref Snapshot baseline, uint changeMask)
        {
            #region __GHOST_CALCULATE_CHANGE_MASK_ZERO__
            changeMask = snapshot.__GHOST_FIELD_NAME__ != baseline.__GHOST_FIELD_NAME__
                ? 1u
                : 0;
            #endregion
            #region __GHOST_CALCULATE_CHANGE_MASK__
            changeMask |= snapshot.__GHOST_FIELD_NAME__ != baseline.__GHOST_FIELD_NAME__
                ? (1u << __GHOST_MASK_INDEX__)
                : 0;
            #endregion
        }

        #if UNITY_EDITOR || NETCODE_DEBUG
        private static void ReportPredictionErrors(ref IComponentData component,
            in IComponentData backup, ref UnsafeList<float> errors, ref int errorIndex)
        {
            #region __GHOST_REPORT_PREDICTION_ERROR__
            errors[errorIndex] = math.max(errors[errorIndex],
                math.abs(component.__GHOST_FIELD_REFERENCE__ -
                    backup.__GHOST_FIELD_REFERENCE__));
            ++errorIndex;
            #endregion
        }

        private static int GetPredictionErrorNames(ref FixedString512Bytes names,
            ref int nameCount)
        {
            #region __GHOST_GET_PREDICTION_ERROR_NAME__
            if (nameCount != 0)
                names.Append(new FixedString32Bytes(","));
            names.Append(new FixedString64Bytes("__GHOST_FIELD_REFERENCE__"));
            ++nameCount;
            #endregion
        }
        #endif
    }
}
```

推荐使用 `CustomNamespace.CustomTemplateFileName` 格式分配 `#templateid`。Netcode for Entities 的所有默认模板都使用格式为 `NetCode.GhostSnapshotValueXXX.cs` 的内部 ID，该 ID 不会出现在模板内

有关模板格式的更多信息，请参阅 `SourceGenerator/Documentation` 文件夹中的文档，或参考 `Editor/Templates/DefaultTypes` 中的其他模板文件

> [!NOTE]
> [默认支持类型](#supported-types)采用与自定义模板略有不同的方式，并嵌入生成器 DLL。模板包含一组类似 C# 的 `#region __GHOST_XXX__` 区域，代码生成器提取 Region 内部代码来创建序列化器。`__GHOST_XXX__` 是模板保留关键字，生成时会替换为对应变量名或值

<a id="defining-subtype-templates"></a>

#### 定义 `SubType` 模板

`SubType` 可以为同一类型定义多个模板，在 `GhostField` 特性中指定即可使用：

```csharp
using Unity.NetCode;

public struct MyComponent : Unity.Entities.IComponentData
{
    [GhostField(SubType = GhostFieldSubType.MySubType)]
    public float ValueWithSubType;

    [GhostField]
    public float ValueWithDefaultUnquantizedSerializer;
}
```

要向项目添加 `SubType`，需实现部分类 [`GhostFieldSubTypes`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostFieldSubType.html)，再使用 [Assembly Definition Reference](https://docs.unity3d.com/Documentation/Manual/class-AssemblyDefinitionReferenceImporter.html) 将其注入 `Unity.NetCode` 程序集。这会向该类添加新的字符串常量，使所有已引用 `Unity.NetCode` 程序集的包都能使用它

```csharp
namespace Unity.NetCode
{
    public static partial class GhostFieldSubType
    {
        public const int MySubType = 1;
    }
}
```

`SubType` 模板的处理方式与其他 `UserDefinedTemplate` 相同，但需要设置 `SubType` 字段索引。详情请参阅[编写模板](#writing-a-template)，唯一差异是需要设置 `SubType = GhostFieldSubType.MySubType`

```csharp
namespace Unity.NetCode.Generators
{
    public static partial class UserDefinedTemplates
    {
        static partial void RegisterTemplates(
            System.Collections.Generic.List<TypeRegistryEntry> templates,
            string defaultRootPath)
        {
            templates.AddRange(new[]
            {
                new TypeRegistryEntry
                {
                    Type = "System.Single",
                    SubType = GhostFieldSubType.MySubType,
                    ...
                },
            });
        }
    }
}
```

与其他模板注册一样，定义 `GhostField` 时必须准确指定与模板匹配的参数。除 `Quantized` 和 `Smoothing` 外，`SubType` 也很重要，因为这些值都会影响如何从模板生成序列化器代码

> [!NOTE]
> `SubType` 的 `Composite` 参数应始终为 `false`，因为系统隐式假设指定模板用于整个类型

<a id="registering-a-template"></a>

### 注册模板

要向 Netcode for Entities 注册模板，实现部分类 [`UserDefinedTemplates`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.Generators.UserDefinedTemplates.html)，再使用 [Assembly Definition Reference](https://docs.unity3d.com/Documentation/Manual/class-AssemblyDefinitionReferenceImporter.html) 将其注入 `Unity.NetCode` 程序集

该部分实现必须定义 `RegisterTemplates` 方法，添加一个或多个 `TypeRegistry` 条目，并位于 `Unity.NetCode.Generators` 命名空间。例如：

```csharp
namespace Unity.NetCode.Generators
{
    public static partial class UserDefinedTemplates
    {
        static partial void RegisterTemplates(
            System.Collections.Generic.List<TypeRegistryEntry> templates,
            string defaultRootPath)
        {
            templates.AddRange(new[]
            {
                new TypeRegistryEntry
                {
                    Type = "MyCustomNamespace.MyCustomType",
                    Quantized = true,
                    Smoothing = SmoothingAction.InterpolateAndExtrapolate,
                    SupportCommand = false,
                    Composite = false,
                    Template = "MyCustomNamespace.MyCustomTypeTemplate",
                    TemplateOverride = "",
                },
            });
        }
    }
}
```

> [!NOTE]
> 该示例只会在 `[GhostField]` 定义为 `[GhostField(Quantization = 100, Smoothing = SmoothingAction.InterpolateAndExtrapolate, Composite = false)]` 时注册 `MyCustomType`
>
> 所有需要支持的精确组合都必须分别注册，并与实际用法完全一致

<a id="template-definition-requirements"></a>

### 模板定义要求

创建模板还必须遵守以下要求：

- 对于 `Serialize`、`Deserialize`、`__COMMAND_WRITE_PACKED__` 和 `__COMMAND_READ_PACKED__`，只能使用 `DataStreamWriter` 与 `DataStreamReader` 的 Packed 和 RawBits 方法。例如，应使用 `WriteRawBits(123, 8)`，不能使用 `WriteByte(123)`。Netcode 会在模板序列化后自行打包数据，因此收发两端的数据流不会具有相同的字节对齐。该限制不适用于未打包 RPC
- `Quantized` 为 `true` 时，模板中必须存在 `__GHOST_QUANTIZE_SCALE__` 变量。通过 `GhostField` 使用该类型时也必须指定量化比例
- `Smoothing` 会改变 `CopyFromSnapshot` 中的序列化处理
  - 设为 `Clamp` 时，只要求 `__GHOST_COPY_FROM_SNAPSHOT__`
  - 设为 `Interpolate` 或 `InterpolateAndExtrapolate` 时，必须实现 `__GHOST_COPY_FROM_SNAPSHOT__`、`__GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE__`、`__GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_SETUP__`、`__GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_DISTSQ__` 和 `__GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_CLAMP_MAX__`
- `SupportCommand` 表示该类型能否用于 Command 或 RPC
- `Template` 是必填项，必须指向目标模板文件中定义的 `#templateid`
- `TemplateOverride` 是可选项，可以为 `null` 或空字符串。需要复用现有模板、只覆盖其中某个 Region 时使用
  - 它很适合 `Composite` 类型：`Template` 可以指向基础类型，例如 `float` 模板，`TemplateOverride` 只指向需要自定义的 Region
  - 例如，`float2` 只定义 `CopyFromSnapshot`、`ReportPredictionErrors` 和 `GetPredictionErrorNames`，其他部分复用基础 `float` 模板，以组合其中两个 `float` 值。指定值必须是另一个模板文件中声明的基础模板 `#templateid`
- 为包含多个相同类型字段的容器类型定义模板时，例如 `float3`、`float4`，`Composite` 应设为 `true`。此时 `Template` 和 `TemplateOverride` 会应用到字段类型，而不是容器类型本身
- 如果模板需要在快照中定义额外字段，例如用于在服务器上正确映射，必须在 Change Mask 计算方法中定义 `__GHOST_CALCULATE_CHANGE_MASK_NO_COMMAND__` 和 `__GHOST_CALCULATE_CHANGE_MASK_ZERO_NO_COMMAND__`。Command 直接指向目标类型，而组件快照可以保存额外数据，这些 Region 能让系统正确找到所有附加字段的 Change Mask。示例请参阅 `GhostSnapshotValueEntity` 模板

所有必需 Region 都必须填写完整

> [!NOTE]
> 修改模板后，需要使用 **Multiplayer** > **Force Code Generation** 菜单强制重新编译代码，使更新后的模板生效
