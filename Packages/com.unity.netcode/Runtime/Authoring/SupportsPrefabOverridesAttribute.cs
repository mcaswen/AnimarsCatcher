using System;

namespace Unity.NetCode
{
    /// <summary>
    /// 使用此特性可<b>允许</b> GhostComponent 支持任意类型的 Ghost 变体
    /// 与 <see cref="DontSupportPrefabOverridesAttribute"/> 互斥
    /// </summary>
    /// <remarks>请注意，如果类型实现 <see cref="GhostComponentVariationAttribute"/>，则会隐式支持 Prefab 覆盖</remarks>
    /// <example>适用场景：在 Ghost 的 `Server` 版本上禁用渲染组件</example>
    [AttributeUsage(AttributeTargets.Struct)]
    [Obsolete("This attribute is now implicit (and thus this attribute does nothing), as all components (including components in other packages) should support user modification, and this prevented that. (RemovedAfter Entities 1.0)")]
    public class SupportsPrefabOverridesAttribute : Attribute
    {
    }
}
