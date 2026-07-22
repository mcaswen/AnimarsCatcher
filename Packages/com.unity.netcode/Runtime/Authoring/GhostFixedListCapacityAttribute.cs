using System;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于显式指示代码序列化限制定长列表容量的特性
    /// </summary>
    [AttributeUsage(AttributeTargets.Field|AttributeTargets.Property, Inherited = true)]
    public class GhostFixedListCapacityAttribute : Attribute
    {
        /// <summary>
        /// 可复制元素的最大数量
        /// 当列表长度超过此阈值时，只复制前 MaxReplicatedElements 个元素
        /// </summary>
        /// <remarks>
        /// MaxReplicatedElements 必须始终小于或等于 64，此限制会在编译阶段强制执行
        /// </remarks>
        public uint Capacity;
    }
}
