# 代码生成规范

<a id="purpose-of-the-document"></a>

## 文档目的

本文概述 Netcode for Entities 代码生成器支持的功能、设计意图，以及 Source Generator 与实体转换或烘焙两端应遵循的行为

<a id="glossary"></a>

## 术语

- `Ghost`：复制实体
- `GhostField`：可添加到结构体成员的特性，表示该成员需要序列化，并声明字段序列化属性
- `GhostComponent`：可添加到结构体或类的特性，用于声明组件剥离及其他复制属性
- `GhostComponentVariation`：用于为组件或 Buffer 类型声明不同序列化方式的特性，通常也称为 `GhostComponentVariant`
- `RPC`：实现 `IRpcCommand` 的结构体
- `Command`：实现 `ICommandData` 的结构体

<a id="purpose"></a>

## 代码生成器职责

代码生成系统负责：

- 为组件、Buffer、Command 和 RPC 生成序列化代码
- 收集并注册组件序列化器及其 Variant
- 生成类型信息，避免运行时反射，并确保兼容 DOTS Runtime 和 Tiny，详情参阅 [Empty Variant](#empty-variants)
- 为 RPC、组件和 Buffer 序列化器生成注册系统

收集到的类型信息会在运行时由以下模块使用：

- `GhostAuthoringConversion`：生成重要的 Ghost 元数据，并从转换后的实体 Prefab 中剥离组件
- `GhostCollectionSystem`：为 Ghost Prefab 构建序列化器、预处理实体 Prefab、分配正确 Variant，并在运行时剥离组件

<a id="code-gen-stack"></a>

## 代码生成技术栈

代码生成的主要目标是自动完成复制数据的序列化与反序列化。整个技术栈由三部分组成：

- Roslyn C# Source Generator：解析 C# AST，并提取构建生成代码所需的类型信息
- Template Framework：根据模板生成类型序列化及相关粘合代码
- User Defined Type Registry：配置各种类型应使用的模板及映射关系

<a id="serialized-types"></a>

## 可序列化类型

只有具有 `public` 可见性的值类型，也就是结构体，才允许生成序列化代码：

```csharp
public struct Serialized
{
}

// internal 类型不会生成序列化器
internal struct NoInternal
{
}

// private 类型不会生成序列化器
private struct NoPrivate
{
}
```

类型还必须实现以下接口之一：

- `IComponentData`
- `IBufferElementData`
- `IRpcCommand`
- `ICommandData`

对于带 [`GhostComponent`](#ghost-component-attribute) 的 [Hybrid Component](#hybrid-components)，还会生成 [Empty Variant](#empty-variants)

<a id="mandatory-requirement"></a>

### 强制要求

同一个类型只能实现上述接口中的一个。以下声明均无效：

```csharp
public struct Invalid1 : IComponentData, IRpcCommand
{
    public int Value1;
}

public struct Invalid2 : IComponentData, ICommandData
{
    public Unity.NetCode.NetworkTick Tick { get; set; }
    public int Value1;
}

public struct Invalid3 : IComponentData, IBufferElementData
{
    public int Value1;
}

public struct Invalid4 : IBufferElementData, ICommandData
{
    public Unity.NetCode.NetworkTick Tick { get; set; }
    public int Value1;
}

public struct Invalid5 : IBufferElementData, IRpcCommand
{
    public int Value1;
}
```

结构体同时实现多个目标接口时，生成器必须报告错误，并指出类型及其实现的接口

不支持泛型接口，例如 `public struct MyTest : MyInterface<AnotherType>`

<a id="hybrid-components"></a>

## Hybrid Component

满足以下条件时，生成器会检查所有继承 `UnityEngine.Component` 或 `UnityEngine.MonoBehaviour` 的类：

- 类声明添加了 `GhostComponentAttribute`
- 类具有 `public` 可见性

**Hybrid Component 不会被序列化**

代码生成器会收集并处理 Hybrid Component，将其放入 [Empty Variant](#empty-variants) 集合。运行时使用这些信息：

- 根据 `GhostComponentAttribute` 属性，从转换后的 Prefab 剥离组件和 Buffer
- 预处理部分类型信息，避免运行时反射，尤其是 Variant Type 和 Component Type，参阅 [GhostComponentVariant](#variant-generation)

<a id="why-we-extract-the-type-information-for-monobehaviour-and-components"></a>

### 为什么提取 MonoBehaviour 与 Component 的类型信息

虽然不推荐使用反射，但 Editor 和传统 Unity Hybrid 构建仍然允许反射。DOTS Runtime 和 Tiny 则无法在运行时通过反射收集特性或其他类型信息

MonoBehaviour 不兼容 DOTS Runtime，也不会进入对应构建，但实体组件与 Hybrid Component 共用部分代码路径，尤其是用于选择 Variant 的路径。实体组件必须兼容 DOTS Runtime，因此两类组件的类型数据都需要提前生成

<a id="namespaces-and-inner-classes"></a>

## 命名空间与嵌套类型

支持多层命名空间：

```csharp
namespace N1.N2.N3
{
    struct A
    {
    }
}
```

支持多层嵌套结构体：

```csharp
namespace N1.N2
{
    struct A
    {
        struct B
        {
            struct C
            {
            }
        }
    }
}
```

生成器内部使用的完整类型名遵循 Roslyn C# 命名标准，包含所有命名空间与声明类型，格式为：

`N1.N2.N3.A+B+C`

<a id="limitation-on-names-and-reserved-keywords"></a>

### 名称与保留关键字限制

- `__GHOST__` 是保留前缀和关键字，字段名与结构体名中都不能包含它
- `__GHOST` 和 `__COMMAND` 是保留前缀，命名空间、类、结构体或成员名都不能以它们开头
- 违反上述规则时必须报告编译错误
- 同名类或结构体可以存在于不同命名空间，无论它们位于相同还是不同程序集
- 名称长度没有特殊限制，但通常应优先使用较短且清晰的名称

<a id="supported-primitive-types"></a>
<a id="supported-fields-types"></a>

## 支持的字段类型

<a id="primitive-types"></a>

### Primitive 类型

- `bool`
- `int`
- `uint`
- `short`
- `ushort`
- `sbyte`
- `byte`
- `long`
- `ulong`
- `float`
- 使用任意整数底层类型的枚举
- Unsafe Fixed Buffer

`char` 因多项实现限制不受支持

<a id="composite-fields-and-type-hierarchies"></a>

### Composite 字段与类型层级

如果序列化类型，也就是 Command、RPC、Component 或 Buffer Element，包含结构体字段，生成器会递归遍历字段类型层级并收集字段

```csharp
struct ChildChildStruct
{
    public int X;
    public int Y;

    [GhostField(SendData = false)]
    public int Z;
}

struct ChildStruct
{
    public int A;
    public ChildChildStruct B;
}

public struct MySerializedStruct : IComponentData
{
    [GhostField]
    public int Field1;

    [GhostField]
    public ChildStruct Field2;
}
```

扁平化后的序列化数据等价于：

```csharp
struct SerializedData
{
    int Field1;
    int Field2_A;
    int Field2_B_X;
    int Field2_B_Y;
}
```

如果目标序列化类型支持 `GhostFieldAttribute`，递归遍历子结构时只收集满足以下条件之一的字段：

- 字段没有 `[GhostField]`
- 字段存在 `[GhostField]`，且 `SendData` 为 `true`

<a id="unitymathematics-types"></a>

### Unity.Mathematics 类型

- `Unity.Mathematics.float2`
- `Unity.Mathematics.float3`
- `Unity.Mathematics.float4`
- `Unity.Mathematics.quaternion`

不支持 `UnityEngine.Vector3`、`UnityEngine.Vector4`、`UnityEngine.Quaternion` 等 UnityEngine 类型

<a id="fixed-strings"></a>

### FixedString

- `FixedString32Bytes`
- `FixedString64Bytes`
- `FixedString128Bytes`
- `FixedString512Bytes`
- `FixedString4096Bytes`

<a id="fixed-list"></a>

### FixedList

- `FixedList32Bytes<T>`
- `FixedList64Bytes<T>`
- `FixedList128Bytes<T>`
- `FixedList512Bytes<T>`
- `FixedList4096Bytes<T>`
- Unsafe Primitive Fixed Buffer

<a id="other-special-fields-types"></a>

### 其他特殊字段类型

支持复制 `Entity` 引用。实体引用是弱引用，如果接收端无法解析对应 Ghost 实例，结果可能为 `Entity.Null`

| 发送端 | 接收端 |
|---|---|
| `Entity.Null` | `Entity.Null` |
| 有效实体 | 对应 Ghost 存在时得到有效实体 |
| 有效实体 | 对应 Ghost 不存在时得到 `Entity.Null` |

<a id="requirements-for-fields-properties-and-accessors"></a>

### 字段、属性与访问器要求

只序列化公开声明的字段和属性，忽略 `private`、`internal` 和 `static` 字段：

```csharp
public struct A
{
    public int F1;
    public float P1 { get; set; }
    public float P2 { get; set; }
    private int F2; // 忽略
    internal int F3; // 忽略
    static int SF1; // 忽略
}
```

不序列化索引器，以及返回声明类型本身的属性：

```csharp
struct A
{
    private int _x;

    public int this[int index]
    {
        get { return _x; }
        set { _x = value; }
    }

    public A ThisIsNotSerialized
    {
        get { return new A(); }
    }
}
```

<a id="type-configuration"></a>

## 类型配置

所有需要生成序列化代码的类型都必须注册到 `TypeRegistry`。Netcode 已配置默认类型，包括全部 Primitive 和部分 Mathematics 类型。用户可以实现 `Unity.NetCode.Generators.UserDefinedTemplates` 方法，提供自定义类型与规则

```csharp
namespace Unity.NetCode.Generators
{
    public static partial class UserDefinedTemplates
    {
        // 在这里添加自定义定义
    }
}
```

Source Generator 负责解析该方法、提取配置并更新类型注册表

<a id="typename-naming-convention"></a>

### 类型名约定

Primitive 类型在模板标识中使用以下特殊名称：

| C# 类型 | 模板名称 |
|---|---|
| `bool` | `System_Boolean` |
| `sbyte` | `System_SByte` |
| `byte` | `System_Byte` |
| `short` | `System_Int16` |
| `ushort` | `System_UInt16` |
| `int` | `System_Int32` |
| `uint` | `System_UInt32` |
| `long` | `System_Int64` |
| `ulong` | `System_UInt64` |
| `float` | `System_Single` |

注册条目中的类型名必须使用完全限定名，包含命名空间和可能存在的声明类：

```csharp
new TypeRegistryEntry
{
    Type = "System.Int32",
    ...
},
new TypeRegistryEntry
{
    Type = "Unity.Mathematics.float3",
    ...
}
```

<a id="syntax-restrictions-for-userdefinedtemplates-implementation"></a>

### `UserDefinedTemplates` 实现的语法限制

由于生成器需要解析 `UserDefinedTemplates.RegisterTemplates` 方法，其实现受以下限制：

- 只能创建并赋值 `TypeRegistryEntry` 结构体
- 只能使用编译期常量

可以多次调用 `templates.Add` 添加模板：

```csharp
templates.Add(new TypeRegistryEntry
{
    Type = "Unity.Mathematics.float3",
    SubType = GhostFieldSubType.Translation2D,
    Quantized = true,
    Smoothing = SmoothingAction.InterpolateAndExtrapolate,
    SupportCommand = false,
    Composite = false,
    Template = "Custom.Translation2d",
    TemplateOverride = "",
});
```

也可以使用 `AddRange`：

```csharp
templates.AddRange(new[]
{
    new TypeRegistryEntry
    {
        Type = "Unity.Mathematics.float3",
        SubType = GhostFieldSubType.Translation2D,
        Quantized = true,
        Smoothing = SmoothingAction.InterpolateAndExtrapolate,
        SupportCommand = false,
        Composite = false,
        Template = "Custom.Translation2d",
        TemplateOverride = "",
    },
    new TypeRegistryEntry
    {
        Type = "Unity.Mathematics.quaternion",
        SubType = GhostFieldSubType.Rotation2D,
        Quantized = true,
        Smoothing = SmoothingAction.InterpolateAndExtrapolate,
        SupportCommand = false,
        Composite = false,
        Template = "Custom.Rotation2d",
        TemplateOverride = "",
    },
});
```

`Template` 和 `TemplateOverride` 路径支持简单字符串插值：

```csharp
Template = $"{MyPath}/Path/ToMyFile";
Template = $"{MyPath}/{Other}/ToMyFile";
```

字符串插值中的所有参数都必须是编译期常量

<a id="subtype-declaration"></a>

### 声明 SubType

可以通过 `GhostField.SubType` 指定 SubType：

```csharp
public struct A
{
    [GhostField(SubType = GhostFieldSubType.MyType)]
    public int MyType;
}
```

生成代码时，SubType 充当过滤或选择条件。生成器会在类型注册表中查找 SubType 值及量化等其他选项均匹配的注册条目

用户可以通过以下方式指定 SubType 值：

- 显式整数值
- 自定义枚举或其他编译期常量
- 通过 ASMREF 扩展部分类 `GhostFieldSubType`，这是推荐方式

唯一要求是该值必须为编译期常量，C# 编译器本身会强制执行

<a id="ghost-component-attribute"></a>

## `GhostComponent` 特性

```csharp
[GhostComponent(
    PrefabType = GhostPrefabType.All,
    SendTypeOptimization = GhostSendType.All,
    OwnerSendType = SendToOwnerType.All)]
```

`GhostComponentAttribute` 可以添加到：

- 实现 `IComponentData` 或 `IBufferElementData` 的结构体
- 实现 `ICommandData` 的结构体，因为它也是 `IBufferElementData`
- 带 `GhostComponentVariation` 的结构体，参阅 [Variant Generation](#variant-generation)
- 继承 `UnityEngine.MonoBehaviour` 或更一般的 `UnityEngine.Component` 的类，参阅 [Hybrid Component](#hybrid-components)

其他情况下会忽略 `GhostComponentAttribute`，代码生成阶段不会检查它

```csharp
[GhostComponent(...)]
public struct Component : IComponentData
{
    ...
}

[GhostComponent(...)]
public struct Buffer : IBufferElementData
{
    ...
}

// 允许剥离 Hybrid Component
[GhostComponent(...)]
public class MyHybridComponent : MonoBehaviour
{
    ...
}

// 允许为 Command Buffer 配置剥离与发送规则
[GhostComponent(...)]
public struct MyCommand : ICommandData
{
    public NetworkTick Tick { get; set; }
}
```

<a id="special-rules-for-icommanddata"></a>

### `ICommandData` 特殊规则

为实现 `ICommandData` 的结构体添加 `GhostComponent` 时：

- `OwnerSendType` 不能设为 `SendToOwnerType.SendToOwner`。生成器会自动移除该标志，并报告编译警告，提示修改配置
- 忽略 `SendToChild`，始终按 `false` 处理

<a id="ghost-field-attribute"></a>

## `GhostField` 特性

```csharp
class GhostFieldAttribute
{
    public int Quantization = -1;
    public SmoothingAction Smoothing = SmoothingAction.Clamp;
    public int SubType = 0;
    public float MaxSmoothingDistance = 0f;
    public bool Composite = false;
    public bool SendData = true;
}
```

该特性可以添加到实现以下接口的结构体成员：

- `IComponentData`
- `IBufferElementData`
- `ICommandData`

<a id="rules"></a>

### 规则

- `Composite` 只能用于结构体类型成员。Primitive 类型会忽略该标志并报告编译警告。**Composite 只影响 Change Mask 计算**
- `MaxSmoothingDistance` 只在 `Smoothing` 为 `Interpolate` 或 `InterpolateAndExtrapolate` 时使用
- `Quantization` 只应用于浮点字段与属性，整数类型会忽略它
- 对 `IBufferElementData` 和 `ICommandData`，始终忽略平滑选项并使用 `Clamp`

<a id="ghost-field-properties-inheritance-rules"></a>

### `GhostField` 属性继承规则

当带 `GhostFieldAttribute` 的成员是 Composite 结构体时，父级特性会按以下规则由子成员继承：

- `SubType` 永不继承，默认始终为 0
- 子字段没有 `GhostFieldAttribute` 时，使用按字段类型适用的父级属性
- 子字段存在 `GhostFieldAttribute` 时，子级属性优先，但仅在以下条件成立时覆盖父级
  - 子级 `Quantization` 大于 0
  - 子级 `Composite` 为 `true`，且类型不是 Primitive
  - 子级 `MaxSmoothingDistance` 大于 0
  - 子级 `Smoothing` 不是 `Clamp`

示例：

```csharp
public struct Child
{
    public int IntField;

    [GhostField]
    public float UseParentQuantization;

    [GhostField(Quantization = 5000)]
    public float UseLocalQuantization;
}

public struct Parent : IComponentData
{
    [GhostField(Quantization = 1000)]
    public Child Child;

    [GhostField(SubType = 5, Quantization = 700,
        Smoothing = SmoothingAction.Interpolate)]
    public float3 CustomFloat3;
}
```

假设类型注册表中存在匹配 `SubType = 5` 的 `float3` 定义，其模板只序列化 `x` 字段，最终结果等价于：

```csharp
struct SerializedData
{
    // 整数不应用量化
    [GhostField]
    public int Child_IntField;

    // 使用父级量化 1000
    [GhostField(Quantization = 1000)]
    public float Child_UseParentQuantization;

    // 使用本地量化 5000
    [GhostField(Quantization = 5000)]
    public float Child_UseLocalQuantization;

    // 使用父级量化、插值与只序列化 x 的自定义模板
    // x 字段本身的 SubType 仍为 0，并匹配默认 float 实现
    [GhostField(Quantization = 700, Smoothing = SmoothingAction.Interpolate)]
    public float CustomFloat3_X;
}
```

<a id="rpc-serialization"></a>

## RPC 序列化

<a id="rpc-syntax"></a>

### 语法

```csharp
public struct MyRpc : IRpcCommand
{
    public int Field1;
    public float Field2;

    [DontSerializeForCommand]
    public int Field3;
}

public struct EmptyRpc : IRpcCommand
{
}

// 支持通过接口继承
public interface IExtendedRpcCommand : IRpcCommand
{
}

public struct MyExtendedRpc : IExtendedRpcCommand
{
    public int Field1;
    public float Field2;

    [DontSerializeForCommand]
    public int Field3;
}
```

<a id="rpc-requirement"></a>

### 要求

- 必须是结构体
- 必须声明为 `public`
- 必须实现 `IRpcCommand`
- 不能包含托管类型，但当前尚未强制检查
- 所有序列化字段必须为 `public`，忽略 `private`、`internal` 和 `static` 字段

<a id="rpc-conditions-to-skip-code-generation"></a>

### 跳过代码生成的条件

- 当前程序集中已经存在 RPC 序列化器类符号时，不得再次生成序列化代码
- 结构体添加 `NetCodeDisableCommandCodeGenAttribute` 时，不生成序列化代码

```csharp
[NetCodeDisableCommandCodeGen]
public struct NoCodeGenerateRpc : IRpcCommand
{
    public int Field1;
    public int Field2;
}
```

<a id="rpc-serialized-fields"></a>

### 序列化字段

RPC 可以包含任意数量的字段，空结构体也有效

- 默认按声明顺序序列化所有 `public` 字段和属性
- 忽略 `private` 和 `static` 字段
- 忽略 `GhostFieldAttribute`
- 带 `DontSerializeForCommandAttribute` 的字段不会序列化

| 功能 | 是否支持 |
|---|---|
| 量化 | 否 |
| 插值 | 否 |
| 外推 | 否 |
| Delta 压缩 | 否 |

支持的字段类型：

- Primitive 类型
- 未量化的 `float2`、`float3`、`float4` 与 `quaternion`
- Netcode 默认模板或用户模板支持的 `FixedString`
- `Entity` 引用
- 在 `UserDefinedTemplates` 中注册且 `SupportCommand` 为 `true` 的类型

RPC 忽略 `GhostComponent` 特性

RPC 模板必须包含 `COMMAND_READ` 和 `COMMAND_WRITE` Region

<a id="component-and-buffer-generation"></a>
<a id="component-and-buffer-serialization"></a>

## Component 与 Buffer 序列化

<a id="component-buffer-syntax"></a>

### 语法

```csharp
public struct Component : IComponentData
{
    [GhostField]
    public int FieldA;

    [GhostField]
    public int FieldB;

    [GhostField(SendData = false)]
    public int NotSerialized;

    [GhostField(Quantization = 1000)]
    public float FieldC;
}

public struct ValidBuffer : IBufferElementData
{
    [GhostField]
    public int FieldA;

    [GhostField]
    public int FieldB;

    [GhostField(Quantization = 1000)]
    public float FieldC;
}

public struct InvalidBuffer : IBufferElementData
{
    public int FieldA; // 会触发编译错误

    [GhostField]
    public int FieldB;
}
```

<a id="component-buffer-requirement"></a>

### 要求

- 必须是结构体
- 必须声明为 `public`
- 必须实现 `IComponentData` 或 `IBufferElementData`

实现 `IComponentData` 的结构体至少有一个字段带 `GhostField` 时，才生成序列化代码。Buffer 还要遵守 [Ghost 全字段或全不标记规则](#ghost-all-fields-or-nothing-rule)

根据上述要求，不得为以下组件类型生成序列化代码：

- Shared Component
- Tag Component
- Chunk Component

<a id="conditions-to-skip-serialization-code-generation"></a>

### 跳过序列化代码生成的条件

- 当前程序集中已经存在生成的 Serializer Variant 类符号
- 组件或 Buffer 的所有字段都没有 `GhostField`

<a id="component-buffer-serialized-fields"></a>

### 序列化字段

- 忽略 `private`、`static` 成员和属性
- 只序列化带 `[GhostField]` 且 `SendData` 为 `true` 的 `public` 字段

| 功能 | Component | Buffer |
|---|---|---|
| 量化 | 支持 | 支持 |
| 插值 | 支持 | 不支持 |
| 外推 | 支持 | 不支持 |
| Delta 压缩 | 支持 | 支持 |
| Huffman 编码 | 支持 | 支持 |

Buffer 不支持插值和外推。声明字段及其 Composite 子字段的 `GhostField.Smoothing` 都会被忽略，并强制为 `Clamp`

支持的字段类型：

- 所有默认支持的 Primitive 类型
- 量化或未量化的 `float2`、`float3`、`float4` 与 `quaternion`
- FixedString
- `Entity` 引用
- `UserDefinedTemplates` 中注册的任意类型

<a id="ghost-all-fields-or_nothing-rule"></a>
<a id="ghost-all-fields-or-nothing-rule"></a>

### Ghost 全字段或全不标记规则

实现 `IBufferElementData` 的结构体只允许两种 `GhostField` 配置：

- 所有字段都添加 `GhostField`
- 所有字段都不添加 `GhostField`

所有字段都添加 `GhostField` 时，`GhostField.SendData` 还必须为 `true`

以下情况会报告编译错误：

- 至少一个成员带 `GhostField`，但其他成员没有
- 任一成员的 `GhostField.SendData` 为 `false`

该规则用于保证新元素加入集合时能够正确初始化。除 0 外没有合理的通用默认值，因此要求所有值都明确传输

`GhostComponent` 必须同时支持 `IComponentData` 和 `IBufferElementData`

<a id="component-buffer-template-regions"></a>

### 模板 Region

用于 Buffer 与 Component 的类型模板需要以下 Region：

| Region | 必需 | 可以为空 |
|---|---|---|
| `GHOST_READ` | 是 | 否 |
| `GHOST_WRITE` | 是 | 否 |
| `GHOST_PREDICT` | 是 | 是 |
| `GHOST_COPY_TO_SNAPSHOT` | 是 | 否 |
| `GHOST_COPY_FROM_SNAPSHOT` | 是 | 否 |
| `GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE` | 是 | 否 |
| `GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_SETUP` | 否 | 是 |
| `GHOST_RESTORE_FROM_BACKUP` | 是 | 否 |
| `GHOST_CALCULATE_CHANGE_MASK_ZERO` | 是 | 否 |
| `GHOST_CALCULATE_CHANGE_MASK` | 是 | 否 |
| `GHOST_REPORT_PREDICTION_ERROR` | 是 | 否 |
| `GHOST_GET_PREDICTION_ERROR_NAME` | 是 | 否 |

<a id="commands-code-generation"></a>

## Command 代码生成

实现 `ICommandData` 的结构体默认会序列化到 Command Stream，并由客户端发送到服务器，参阅 [Command 序列化](#command-serialization)

某些场景还需要让会话中的其他玩家收到这些 Command，例如预测远端玩家。只要**所有字段**都添加 `GhostField`，实现 `ICommandData` 的结构体就可以作为 Input Buffer 序列化进 Ghost Snapshot，参阅 [Command Buffer 序列化](#command-buffer-serialization)

与普通 `IBufferElementData` 不同，客户端不会在服务器快照中收到自己的 Input Buffer。`GhostComponent.OwnerSendType` 会隐式强制为 `SendToOwnerType.SendToNotOwner`

当 `ICommandData` 同时作为客户端到服务器的 Command 和服务器到其他客户端的 Input Buffer 发送时，两条路径的序列化数据存在差异。Input Buffer 使用 `GhostField` 属性生成序列化代码，因此浮点字段可以量化。若结构体包含浮点字段，服务器与其他远端玩家收到的数据可能不同

```csharp
public struct MyCommand : ICommandData
{
    [GhostField]
    public Unity.NetCode.NetworkTick Tick { get; set; }

    [GhostField]
    public float AllTheSame;

    [GhostField(Quantization = 100)]
    public float QuantizedForRemotePlayers;
}
```

`AllTheSame` 在本地客户端、服务器与其他远端玩家上相同。`QuantizedForRemotePlayers` 则不同：

- 服务器通过 Command Stream 收到未量化值
- 其他远端玩家通过 Ghost Snapshot 收到量化值

如果该数据用于预测循环，量化会造成轻微不同的预测结果。大多数情况下差异不明显，因为预测本身就是近似

<a id="command-serialization"></a>

## Command 序列化

<a id="command-syntax"></a>

### 语法

```csharp
public struct MyCommand : ICommandData
{
    public NetworkTick Tick { get; set; }
    public int Field1;
    public float Field2;

    [DontSerializeForCommand]
    public int Field3;
}

// 支持通过接口继承
public interface IExtendedCommandData : ICommandData
{
}

public struct MyExtendedCommand : IExtendedCommandData
{
    public NetworkTick Tick { get; set; }
    public int Field1;
    public float Field2;

    [DontSerializeForCommand]
    public int Field3;
}
```

### 要求

- 必须是结构体
- 必须声明为 `public`
- 必须实现 `ICommandData`
- 不能包含托管类型，但当前尚未强制检查
- 所有序列化字段必须为 `public`，忽略 `private`、`internal` 和 `static` 字段

### 跳过代码生成的条件

- 当前程序集中已经存在 Command Serializer 类符号时，不得再次生成序列化代码
- 结构体添加 `NetCodeDisableCommandCodeGenAttribute` 时，不生成序列化代码

```csharp
[NetCodeDisableCommandCodeGen]
public struct NotGenerated : ICommandData
{
    public NetworkTick Tick { get; set; }
    public int Field1;
    public int Field2;
}
```

### 序列化字段

- 默认按声明顺序序列化所有 `public` 字段和属性
- 忽略 `private` 和 `static` 字段
- 忽略 `GhostFieldAttribute`
- 带 `DontSerializeForCommandAttribute` 的字段不序列化

| 功能 | 是否支持 |
|---|---|
| 量化 | 否 |
| 插值 | 否 |
| 外推 | 否 |
| Delta 压缩 | 支持 |

支持的字段类型：

- Primitive 类型
- 未量化的 `float2`、`float3`、`float4` 与 `quaternion`
- FixedString
- `Entity` 引用
- `UserDefinedTemplates` 中注册且 `SupportCommand` 为 `true` 的类型

<a id="commands-delta-compression"></a>

### Command Delta 压缩

Command 从客户端发送到服务器时会使用当前压缩模型进行 Delta 压缩和打包：

- 第一条 Command 及其 Tick 不进行编码或压缩
- Buffer 中后续 N 条 Command 和 Tick 相对第一条 Command 进行 Delta 压缩，第一条充当 Baseline；N 由窗口大小决定，默认值为 3

Command 支持 `GhostComponent`，但部分属性受限：

- 支持 `PrefabType`，并据此从 Ghost 剥离底层 Dynamic Buffer
- 支持 `SendTypeOptimization`
- 忽略不适用的 `OwnerSendType`

Command 模板必须包含：

- `COMMAND_READ`
- `COMMAND_WRITE`
- `COMMAND_READ_PACKED`
- `COMMAND_WRITE_PACKED`

<a id="command-buffer-serialization"></a>

## Command Buffer 序列化

### 语法

```csharp
public struct RemotePlayerCommand : ICommandData
{
    [GhostField]
    public NetworkTick Tick { get; set; }

    [GhostField]
    public int Field1;

    [GhostField]
    public float Field2;

    [GhostField(Quantization = 1000)]
    public float Field3;

    [DontSerializeForCommand]
    [GhostField]
    public int SnapshotOnlyField;
}
```

只要为一个或多个 Command 字段添加 `GhostField`，就会启用 Input Buffer 序列化。启用后，Command Dynamic Buffer 会作为服务器 Ghost Snapshot 的一部分序列化，并发送给其他远端玩家

`ICommandData` 同时也是 `IBufferElementData`，因此代码生成要求与 [Buffer](#component-and-buffer-generation) 相同

### 要求

- 必须是结构体
- 必须声明为 `public`
- 必须实现 `ICommandData`
- 所有字段都必须添加 `GhostField`，参阅 [Ghost 全字段或全不标记规则](#ghost-all-fields-or-nothing-rule)

### 跳过代码生成的条件

当前程序集中已经存在 Command Serializer 类符号时，不得再次生成序列化代码

### 序列化字段

- 忽略 `private`、`static` 成员和属性
- 序列化所有带 `GhostField` 的 `public` 字段与属性

| 功能 | 是否支持 |
|---|---|
| 量化 | 支持 |
| 插值 | 不支持 |
| 外推 | 不支持 |
| Delta 压缩 | 支持 |
| Huffman 编码 | 支持 |

不支持插值和外推。字段及其 Composite 子字段的 `GhostField.Smoothing` 会被忽略，并强制为 `Clamp`

支持的字段类型：

- Primitive 类型
- 量化或未量化的 `float2`、`float3`、`float4` 与 `quaternion`
- FixedString
- `Entity` 引用
- `UserDefinedTemplates` 中注册的所有类型

Command Buffer 支持 `GhostComponent`，但部分属性受限：

- 支持 `PrefabType`，并据此从 Ghost 剥离底层 Dynamic Buffer
- 支持 `SendTypeOptimization`
- `OwnerSendType` 只能是 `SendToOwnerType.SendToNotOwner`。生成器会修改生成标志来强制执行，并警告用户修正配置

<a id="variant-generation"></a>

## Variant 生成

### 语法

```csharp
[GhostComponentVariation(typeof(OriginalType))]
public struct MySerializationVariant
{
    [GhostField]
    public int FieldA;

    [GhostField]
    public int FieldB;

    [GhostField(Quantization = 1000)]
    public float FieldC;
}
```

声明的 `GhostComponentVariation` 类型只用于指示代码生成器为 `OriginalType` 构建不同的序列化方式，不应承担其他职责

### 要求

- 必须是结构体
- 必须声明为 `public`
- 必须带 `GhostComponentVariation`
- 必须声明 Variant 对应的原始类型
- 不能包含原始类型中不存在的字段

原始类型必须是以下之一：

- 实现 `IComponentData` 或 `IBufferElementData` 的 `public` 结构体
- `public` [Hybrid Component](#hybrid-components)

以下情况下，Variant 不必声明原始类型的全部字段：

- 原始类型实现 `IComponentData`
- 原始类型是 Hybrid Component

以下情况会报告编译错误：

- `GhostComponentVariation` 声明了原始类型中不存在的属性或字段
- 原始类型不是 `public`
- 原始类型声明了 `DontSupportPrefabOverridesAttribute`

```csharp
[DontSupportPrefabOverrides]
public struct OriginalType : IComponentData
{
}
```

当前程序集中已经存在生成的 Serializer Variant 类符号时，跳过序列化代码生成

<a id="special-rules-for-buffers"></a>

### Buffer 特殊规则

原始类型实现 `IBufferElementData` 时，Variant 声明同样受全部 Buffer 限制，尤其会强制执行 [Ghost 全字段或全不标记规则](#ghost-all-fields-or-nothing-rule)：

- `GhostComponentVariation` 必须声明原始类型的所有字段
- 所有字段都必须添加 `GhostField`

任一成员未添加 `GhostField` 时会报告编译错误

<a id="variant-ghost-component-support"></a>

### `GhostComponent` 支持

`GhostComponentVariation` 允许在 Variant 结构体上添加 `GhostComponent`：

```csharp
[GhostComponentVariation(typeof(OriginalType))]
[GhostComponent(...)]
public struct MySerializationVariant
{
    [GhostField]
    public int FieldA;

    [GhostField]
    public int FieldB;

    [GhostField(Quantization = 1000)]
    public float FieldC;
}
```

`GhostComponent` 属性会像普通 Component 和 Buffer 一样反映到生成的 Serializer Variant 代码中

Variant 的序列化字段与模板 Region 规则参阅 [Component 与 Buffer](#component-and-buffer-generation)

<a id="empty-variants"></a>

## Empty Variant

以下类型会被视为 Empty Variant：

- [Component、Buffer](#component-and-buffer-serialization) 或 [Command Buffer](#command-buffer-serialization)，同时满足
  - 没有任何序列化字段，也就是不存在 `[GhostField]`
  - 带 `GhostComponent`
- [Variant](#variant-generation)，同时满足
  - 没有字段，或者所有字段都没有 `[GhostField]`
- [Hybrid Component](#hybrid-components)，同时满足
  - 带 `GhostComponent`

Empty Variant 不生成序列化代码，只用于追踪 Netcode 运行时需要的重要类型信息：

- Variant Type：声明 Variant 的类或结构体类型，用于避免运行时反射
- Component Type：Variant 对应的组件类型，用于避免运行时反射
- Variant Hash：在 Inspector 和其他场景中关联 Variant
- `GhostPrefabType`：在转换或运行时从 Prefab 剥离组件

<a id="create-empty-variant-using-a-ghostcomponent-attribute"></a>

### 使用 `GhostComponent` 创建 Empty Variant

为[可序列化类型](#serialized-types)或 [Hybrid Component](#hybrid-components)添加 `GhostComponent`，但不把任何字段标记为序列化：

```csharp
[GhostComponent(...)]
public struct MyEmptyComponentVariant : IComponentData
{
    public int FieldA;
    public int FieldB;
}

[GhostComponent(...)]
public struct MyEmptyBufferVariant : IBufferElementData
{
    public int FieldA;
    public int FieldB;
}

[GhostComponent(...)]
public struct MyEmptyCommandVariant : ICommandData
{
    public NetworkTick Tick { get; set; }
    public int FieldA;
    public int FieldB;
}
```

需要注意：

- Buffer 和 Command 都遵守 [Ghost 全字段或全不标记规则](#ghost-all-fields-or-nothing-rule)
- 为 `ICommandData` 添加 `GhostComponentAttribute` 不影响 Command Stream 序列化
  - 仍然会为 Command 生成序列化代码
  - 不会发送给其他玩家，因为没有生成 Buffer 序列化代码

<a id="create-empty-variant-using-ghostcomponentvariation"></a>

### 使用 `GhostComponentVariation` 创建 Empty Variant

声明没有字段、没有 `GhostField`，或所有 `GhostField.SendData` 都为 `false` 的 `GhostComponentVariation`。`GhostComponentAttribute` 不是必需项：

```csharp
[GhostComponentVariation(typeof(MyStruct))]
[GhostComponent(...)] // 可选
public struct MyStructEmptyVariant
{
    public int FieldA;
    public int FieldB;
}

[GhostComponentVariation(typeof(MyStruct))]
[GhostComponent(...)] // 可选
public struct MyStructEmptyVariantWithoutFields
{
}

[GhostComponentVariation(typeof(MyBuffer))]
[GhostComponent(...)] // 可选
public struct MyBufferEmptyVariant
{
    [GhostField(SendData = false)]
    public int FieldA;

    [GhostField(SendData = false)]
    public int FieldB;
}
```

为 Hybrid Component 声明 `GhostComponentVariation` 也会生成 Empty Variant：

```csharp
[GhostComponentVariation(typeof(MyHybridComponent))]
[GhostComponent(...)] // 可选
public struct MyHybridEmptyVariant
{
}
```

Hybrid Component 默认不能序列化，因此不需要在 Variant 中指定任何字段
