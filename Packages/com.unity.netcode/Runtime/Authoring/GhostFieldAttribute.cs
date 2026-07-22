using System;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于指定应复制 <see cref="Unity.Entities.IComponentData"/> 或
    /// <see cref="Unity.Entities.IBufferElementData"/> 的哪些字段和属性，以及如何复制
    /// 当组件或 Buffer 至少包含一个使用 <see cref="GhostFieldAttribute"/> 标注的字段时，
    /// 系统会自动生成实现该组件序列化的结构体代码
    /// </summary>
    /// <remarks>请注意，即使可启用组件（<see cref="Unity.Entities.IEnableableComponent"/>）处于禁用状态，其字段仍会被复制
    /// 如需复制启用标志本身，请参见 <see cref="GhostEnabledBitAttribute"/></remarks>
    [AttributeUsage(AttributeTargets.Field|AttributeTargets.Property)]
    public class GhostFieldAttribute : Attribute
    {
        /// <summary>
        /// 浮点数会先乘以此数值再舍入为整数，以便通过 Huffman 编码获得更好的差分压缩效果
        /// 整数不支持量化，浮点数默认也不启用量化
        /// 如需发送未量化的浮点数，请使用 0
        /// 示例：
        /// Quantization=0 表示完整精度
        /// Quantization=1 表示精度为 1f，即将浮点值舍入为整数
        /// Quantization=2 表示精度为 0.5f
        /// Quantization=10 表示精度为 0.1f
        /// Quantization=20 表示精度为 0.05f
        /// Quantization=1000 表示精度为 0.001f
        /// </summary>
        public int Quantization { get; set; } = -1;

        /// <summary>
        /// 仅适用于添加到包含多个字段的非基元结构体上的 GhostFieldAttribute
        /// 如果未设置此值，即使用默认值 false，则嵌套结构体中的每个字段都会分别包含一个变化位
        /// 结构体本身不会拥有变化位
        /// 也就是说，如果子结构体内只有一个字段发生变化，则只设置该字段的变化位
        /// 如果将 Composite 设为 true，则改为对整个嵌套结构体使用一个变化位
        /// 也就是说，只要子结构体内任一字段发生变化，就会设置整个结构体的单个变化位
        /// 示例可查看 Library\NetCodeGenerated_Backup 中生成的 Serialize/Deserialize 方法
        /// </summary>
        public bool Composite { get; set; } = false;

        /// <summary>
        /// 默认值为 <see cref="SmoothingAction.Clamp"/>
        /// </summary>
        /// <inheritdoc cref="SmoothingAction"/>
        public SmoothingAction Smoothing { get; set; } = SmoothingAction.Clamp;

        /// <summary>

        /// 允许使用 <see cref="GhostFieldSubType"/> API 为此 GhostField 指定自定义序列化器

        /// </summary>
        /// <inheritdoc cref="GhostFieldSubType"/>
        public int SubType { get; set; } = GhostFieldSubType.None;
        /// <summary>
        /// 默认值为 true，如果设为 false，则指示代码生成器不要在序列化数据中包含此字段
        /// 也就是说，不复制此字段
        /// 这对于结构体等非基元成员尤其有用，因为它们默认会序列化所有字段
        /// </summary>
        public bool SendData { get; set; } = true;

        /// <summary>
        /// 允许在两个 Snapshot 之间应用平滑的最大距离
        /// 如果两个已接收 Snapshot 之间的值变化超过此距离，则不会执行平滑操作
        /// </summary>
        /// <remarks>
        /// 对于四元数，指定值应为 sin(theta / 2)，其中 theta 是需要应用平滑的最大角度
        /// </remarks>
        public float MaxSmoothingDistance { get; set; } = 0;
    }

    /// <summary>
    /// 表示应复制 <see cref="Unity.Entities.IEnableableComponent"/> 启用标志的特性
    /// 因此该特性仅适用于可启用组件类型，否则会产生编译器错误
    /// </summary>
    /// <remarks>只有在类上添加此特性，类型才会复制其启用标志
    /// 对序列化启用位的变体，也可以且应该添加此特性</remarks>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
    public sealed class GhostEnabledBitAttribute : Attribute
    {
    }

    /// <summary>
    /// 添加此特性可阻止序列化 ICommandData 结构体中的字段
    /// </summary>
    [AttributeUsage(AttributeTargets.Field|AttributeTargets.Property, Inherited = true)]
    public class DontSerializeForCommandAttribute : Attribute
    {
    }
}
