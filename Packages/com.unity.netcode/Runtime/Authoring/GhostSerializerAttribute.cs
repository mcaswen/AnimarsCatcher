using System;

namespace Unity.NetCode
{
    /// <summary>
    /// 仅供内部使用
    /// 用于标记生成的组件或 Buffer 序列化器，由代码生成系统自动添加
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public class GhostSerializerAttribute : Attribute
    {
        /// <summary>
        /// 此序列化器对应的组件类型
        /// </summary>
        public readonly Type ComponentType;
        /// <summary>
        /// 为此序列化器计算出的变体哈希值
        /// 如果序列化代码由组件类型声明生成，则此字段为 0
        /// </summary>
        public readonly ulong VariantHash;

        /// <summary>
        /// 构造此特性并分配组件类型和变体哈希值
        /// </summary>
        /// <param name="componentType">此序列化器对应的组件类型</param>
        /// <param name="variantHash">为此序列化器计算出的变体哈希值</param>
        public GhostSerializerAttribute(Type componentType, ulong variantHash)
        {
            ComponentType = componentType;
            VariantHash = variantHash;
        }
    }
}
