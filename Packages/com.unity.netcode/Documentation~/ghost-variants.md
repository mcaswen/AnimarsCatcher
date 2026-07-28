# 使用 `GhostComponentVariationAttribute` 创建复制模式

使用 [`GhostComponentVariationAttribute`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostComponentVariationAttribute.html)，可以在编译时为某个类型声明复制模式，而无需标记原始类型或原始类型中的字段。这些复制模式称为变体。对于代码生成，新声明的模式相当于代理：代码生成系统不再直接使用原始类型，而是使用声明的变体生成特定版本的序列化代码

变体依赖 [`GhostFieldAttribute`](ghostfield-synchronize.md) 和 [`GhostComponentAttribute`](ghostcomponentattribute.md)，建议创建变体前先了解这两个主题。也可以使用 [Ghost 类型模板](ghost-types-templates.md)管理自定义序列化，但其实现更加复杂，只建议高级用户使用

> [!NOTE]
> 目前尚未完整支持 `IBufferElementData` 的 Ghost 组件变体

<a id="variant-use-cases"></a>
## 变体用例

`GhostComponentVariationAttribute` 主要用于以下场景：

* 为无法直接修改的组件声明序列化规则，例如包或外部程序集中的组件。比如，可以通过变体复制 [`Unity.Entities.LocalTransform`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Transforms.LocalTransform.html)
* 为同一类型生成多种序列化策略，使每个 Ghost 可以选择自己的版本。例如，只复制 [`Unity.Entities.LocalRotation`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.TransformAuthoring.LocalRotation.html) 的偏航值，或复制完整 `quaternion`
* 通过覆盖或向类型添加 [`GhostComponentAttribute`](ghostcomponentattribute.md)，从特定预制体类型中移除组件，而无需修改原始声明

<a id="example"></a>
### 示例

```c#
[GhostComponentVariation(typeof(LocalTransform), "Transform - 2D")]
[GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
public struct PositionRotation2d
{
    [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate, SubType=GhostFieldSubType.Translation2D)]
    public float3 Position;
    [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate, SubType=GhostFieldSubType.Rotation2D)]
    public quaternion Rotation;
}
```

上例中，`PositionRotation2d` 变体使用变体声明中的属性和特性，为 `LocalTransform` 生成序列化代码

特性构造函数接收以下参数：

* 要为其指定变体的 `ComponentType` 对应的 `Type type`，本例为 `LocalTransform`
* `string variantName`，用于指定便于阅读的名称，并显示在 [`GhostAuthoringInspectionComponent`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringInspectionComponent.html) UI 中

随后，对原始结构体中需要复制的每个字段，本例为 `LocalTransform`，添加 [`GhostFieldAttribute`](ghostfield-synchronize.md)，并以与基础结构体完全相同的方式定义字段。还可以选择向变体添加 [`GhostComponentAttribute`](ghostcomponentattribute.md)，进一步指定组件序列化属性

> [!NOTE]
> 只允许声明组件类型中实际存在的成员。系统会在编译时验证，违反该规则会抛出异常

可以为一个组件声明多个序列化变体，例如为 `LocalRotation` 同时提供 2D 和 3D 变体。如果某个 `ComponentType` 只定义了一个变体，该变体会自动成为该类型的默认序列化策略

<a id="specifying-which-variant-to-use-on-a-prefab"></a>
## 指定预制体使用的变体

可以使用 [`GhostAuthoringInspectionComponent`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringInspectionComponent.html) 按预制体指定使用哪个变体。可以为每个组件分别选择变体，其中包括特殊变体 `DontSerializeVariant`

向 GameObject 添加 `GhostAuthoringInspectionComponent` 后，Unity 编辑器会显示运行时实体上哪些组件会被复制，并允许修改以下属性：

* 应添加并复制该组件的 `GhostPrefabType`，通过切换按钮设置：`S` 表示服务器、`IC` 表示插值客户端、`PC` 表示预测客户端。详细信息请参阅 [`PrefabType` 详解](ghostcomponentattribute.md#prefabtype-details)
* 该组件的 `GhostSendType`，即 `Send Optimization` 下拉菜单，以及适用时的 `SendToOwnerType`，即 `Send to Owner` 下拉菜单
* 该组件使用的序列化 `Variant` 下拉菜单，其中包括[内置变体类型](#special-variant-types)

![Ghost Authoring 变体](images/ghost-inspection.png)

下拉菜单会显示该组件类型的全部可用变体。默认不序列化子实体上的组件。若要修改 Ghost 预制体子项的复制方式，请为每个子项分别添加 `GhostAuthoringInspectionComponent`

> [!NOTE]
> `GhostAuthoringInspectionComponent` 也是很有价值的调试工具。将其添加到 Ghost 预制体或某个子项，可以查看该 Ghost 上的全部复制类型，并诊断特定类型未按预期复制的原因

<a id="special-variant-types"></a>
### 特殊变体类型

以下内置变体类型具有特定行为：

| 内置变体 | 说明 |
|----------|------|
| `ClientOnlyVariant` | 指定某个 `ComponentType` 只应出现在客户端 World 中 |
| `ServerOnlyVariant` | 指定某个 `ComponentType` 只应出现在服务器 World 中 |
| `DontSerializeVariant` | 完全禁用某个类型的序列化，并忽略复制特性 `[GhostField]` 和 `[GhostEnabledBit]` |

```C#
using System.Collections.Generic;
using Unity.Entities;
using Unity.Transforms;

namespace Unity.NetCode.Samples
{
    sealed class DefaultVariantSystem : DefaultVariantSystemBase
    {
        protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
        {
            defaultVariants.Add(typeof(SomeClientOnlyThing), Rule.ForAll(typeof(ClientOnlyVariant)));
            defaultVariants.Add(typeof(SomeServerOnlyThing), Rule.ForAll(typeof(ServerOnlyVariant)));
            defaultVariants.Add(typeof(NoNeedToSyncThis), Rule.ForAll(typeof(DontSerializeVariant)));
        }
    }
}
```

也可以通过 `GhostAuthoringInspectionComponent`，在 Ghost 预制体的 Ghost 组件上手动选择 `DontSerializeVariant`

<a id="preventing-a-component-from-supporting-variations"></a>
### 禁止组件支持变体

某些情况下，需要禁止通过变体修改组件序列化。例如，为确保 [`GhostInstance`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostInstance.html) 始终正确序列化，Netcode for Entities 禁止用户代码修改其序列化规则

若要禁止组件支持变体，请使用 [`DontSupportPrefabOverridesAttribute`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.DontSupportPrefabOverridesAttribute.html)。如果为该类型定义 `GhostComponentVariation`，编译时会报告错误

<a id="assigning-a-default-variant-to-use-for-a-type"></a>
### 为类型指定默认变体

如果某个类型存在多个变体，Netcode for Entities 可能无法推断应使用哪个变体进行序列化。如果该类型的默认序列化器会被复制，它就会成为默认变体；否则会视为冲突，并在创建任何 World 时产生运行时异常，其中包括烘焙 World。Netcode for Entities 会使用确定性的回退方法猜测变体，但通常仍由用户负责明确指定默认变体

若要指定某个类型的默认变体，需要创建继承自 [`DefaultVariantSystemBase`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.DefaultVariantSystemBase.html) 的系统，并实现 `RegisterDefaultVariants` 方法。例如：

```c#
using System.Collections.Generic;
using Unity.Entities;
using Unity.Transforms;

namespace Unity.NetCode.Samples
{
    sealed partial class DefaultVariantSystem : DefaultVariantSystemBase
    {
        protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
        {
            defaultVariants.Add(typeof(LocalTransform), Rule.OnlyParents(typeof(TransformDefaultVariant)));
        }
    }
}
```

上例确保 `LocalTransform` 默认使用 `TransformDefaultVariant`。详细信息请参阅 [`DefaultVariantSystemBase` 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.DefaultVariantSystemBase.html)

> [!NOTE]
> 这是在整个项目范围内指定 Ghost 默认变体的推荐方法。应优先使用 `DefaultVariantSystemBase`，而不是通过 `GhostAuthoringInspectionComponent` 覆盖直接修改变体

## 其他资源

* [`GhostComponentVariationAttribute` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostComponentVariationAttribute.html)
* [`GhostAuthoringInspectionComponent` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringInspectionComponent.html)
* [使用 `GhostComponentAttribute` 自定义复制行为](ghostcomponentattribute.md)
* [使用 `GhostFieldAttribute` 进行序列化与同步](ghostfield-synchronize.md)
* [Ghost 类型模板](ghost-types-templates.md)
