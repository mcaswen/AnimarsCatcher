namespace Unity.NetCode.Generators
{
    /// <summary>
    /// <para>用于配置特定类型（基元或结构体）及 <see cref="GhostFieldAttribute"/> 量化、平滑和子类型标志组合的序列化与反序列化代码生成
    /// 元组 [<see cref="Type"/>, <see cref="Quantized"/>, <see cref="Smoothing"/>, <see cref="SubType"/>] 会映射到一个模板文件，
    /// 该文件包含序列化和反序列化此特定类型所使用的代码
    /// 因而可以为每种类型注册多条序列化规则，并通过 <see cref="GhostFieldAttribute"/> 选择
    /// 例如，默认 float 类型（子类型 0）具有以下 4 条不同的序列化规则：</para>
    /// <para>(float, unquantized, Clamp, 0)</para>
    /// <para>(float, unquantized, InterpolateAndExtrapolate, 0)</para>
    /// <para>(float, quantized, Clamp, 0)</para>
    /// <para>(float, quantized, InterpolateAndExtrapolate)</para>
    /// </summary>
    public class TypeRegistryEntry
    {
        /// <summary>
        /// 必填，类型的限定名称，即命名空间加类型名称
        /// </summary>
        public string Type;
        /// <summary>
        /// 必填，要使用的模板文件，必须是相对于 Asset 或 Package 文件夹的路径
        /// </summary>
        public string Template;
        /// <summary>
        /// 可选，用于覆盖或修改基础 <see cref="Template"/> 文件中序列化代码的模板文件
        /// 必须是相对于 Asset 或 Package 文件夹的路径
        /// </summary>
        public string TemplateOverride;
#pragma warning disable 649
        /// <summary>
        /// 此特定类型与模板组合的子类型值
        /// 用于把 <see cref="GhostFieldAttribute"/> 属性指定的 [type, Quantized, Smoothing, SubType] 元组
        /// 映射到正确的序列化器类型
        /// </summary>
        public int SubType;
#pragma warning restore 649
        /// <summary>
        /// 此模板与类型组合支持的平滑方式
        /// </summary>
        public SmoothingAction Smoothing;
        /// <summary>
        /// <para>浮点数可以通过两种方式序列化：</para>
        /// <para>- 作为完整的 32 bit 原始值</para>
        /// <para>- 作为指定精度的定点数，参见 <see cref="GhostFieldAttribute.Quantization"/></para>
        /// <para>使用量化需要代码生成器进行特殊处理，尤其要求模板文件中的代码遵循特定规则
        /// 如果类型与模板组合应该用于量化类型，则应将此标志设为 true</para>
        /// </summary>
        public bool Quantized;
        /// <summary>
        /// 表示序列化 Command 时能否使用此类型与模板配对
        /// </summary>
        public bool SupportCommand;
        /// <summary>
        /// 表示此类型与模板配对是否为复合类型，只能用于包含多个相同类型字段的结构体，例如 float3
        /// 将类型配置为复合类型后，会在所有字段上递归使用 <see cref="Template"/> 模型生成序列化代码，
        /// 无需为结构体本身创建专用模板
        /// </summary>
        public bool Composite;
    }
}
