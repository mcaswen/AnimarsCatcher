using System;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>使用变体声明中的 <see cref="GhostFieldAttribute"/> 标注为组件生成序列化变体
    /// 可在创作阶段通过 GhostAuthoringComponent 编辑器分配组件变体</para>
    /// <para>注意：这与任何实现 <see cref="DontSupportPrefabOverridesAttribute"/> 的类型不兼容</para>
    /// </summary>
    /// <remarks>
    /// 声明变体时，必须声明所有需要序列化的字段
    /// 任何遗漏的字段或原始结构体中不存在的新字段都不会被序列化
    /// </remarks>
    [AttributeUsage(AttributeTargets.Struct)]
    public class GhostComponentVariationAttribute : Attribute
    {
        /// <summary>
        /// 此变体要覆盖的类型，在构造时分配
        /// </summary>
        public readonly Type ComponentType;

        /// <summary>
        /// 对用户友好且易读的变体名称，主要用于 UI 和日志
        /// 如果构造时未分配，则改用带标注的类名
        /// Default、ClientOnly 和 DontSerialize 不是有效名称，将按 null 处理
        /// </summary>
        /// <example>"Translation - 2D"</example>
        public string DisplayName { get; internal set; }

        /// <summary>
        /// 组件变体的唯一哈希值
        /// 该哈希值在编译阶段计算并分配给生成的序列化类，
        /// 随后在 <see cref="GhostComponentSerializerCollectionSystemGroup"/> 注册全部变体时，
        /// 于运行时分配给此特性
        /// 编辑阶段和运行时都会使用该哈希值识别每个组件所用的变体
        /// </summary>
        public ulong VariantHash { get; internal set; }

        /// <summary>
        /// 如果仅供编辑器使用则为 true，此时会在面向用户的下拉列表中隐藏该变体
        /// 如果为 true 且找不到合适的默认变体，则会在编辑器中将此变体设为默认值
        /// </summary>
        public bool IsTestVariant { get; internal set; }

        /// <summary>
        /// 为指定组件类型初始化并声明变体
        /// 由于无法约束到特定接口，目前会在构造函数中于编译阶段执行验证
        /// </summary>
        /// <param name="componentType"><see cref="ComponentType"/></param>
        /// <param name="displayName"><see cref="DisplayName"/></param>
        /// <param name="isTestVariant"><see cref="IsTestVariant"/></param>
        public GhostComponentVariationAttribute(Type componentType, string displayName = null, bool isTestVariant = false)
        {
            if (string.Equals(displayName, GhostVariantsUtility.k_DefaultVariantName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(displayName, GhostVariantsUtility.k_DontSerializeVariant, StringComparison.OrdinalIgnoreCase)
                || string.Equals(displayName, GhostVariantsUtility.k_ClientOnlyVariant, StringComparison.OrdinalIgnoreCase))
                displayName = null;

            ComponentType = componentType;
            DisplayName = displayName;
            IsTestVariant = isTestVariant;
        }
    }
}
