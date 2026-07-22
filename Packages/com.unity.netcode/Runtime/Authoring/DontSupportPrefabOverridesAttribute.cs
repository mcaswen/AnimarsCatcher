using System;

namespace Unity.NetCode
{
    /// <summary>
    /// 使用此特性可阻止 GhostComponent 支持任何变体或 PrefabType 覆盖
    /// 同时会在 `GhostAuthoringInspectionComponent` 窗口中隐藏该组件
    /// 与 <see cref="SupportsPrefabOverridesAttribute"/> 互斥
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
    public sealed class DontSupportPrefabOverridesAttribute : Attribute
    {
    }
}
