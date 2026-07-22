using System;
using Unity.Entities;
using Unity.Collections;
using Unity.Jobs;

namespace Unity.NetCode
{
    /// <summary>
    /// 指定如何使用加入相关性集合的 Ghost
    /// </summary>
    public enum GhostRelevancyMode
    {
        /// <summary>
        /// 默认值，任何情况下都不应用相关性筛选
        /// </summary>
        Disabled,
        /// <summary>
        /// 只有加入相关性集合 GhostRelevancySet 的 Ghost 才被视为与该客户端相关
        /// 并在最终一致性和 Importance 缩放规则允许时为指定连接序列化
        /// </summary>
        /// <remarks>
        /// 注意，此设置会使所有 Ghost 默认不向任何客户端复制
        /// 当玩家很少或不可能观察整个世界时，这是一个实用的默认策略
        /// </remarks>
        SetIsRelevant,
        /// <summary>
        /// 加入相关性集合 <see cref="GhostRelevancy.GhostRelevancySet"/> 的 Ghost 被视为与该客户端不相关
        /// 因此不会为指定连接序列化
        /// 如果需要让某个客户端明确忽略特定实体，请使用此模式
        /// </summary>
        SetIsIrrelevant
    }

    /// <summary>
    /// 连接与 Ghost 的组合，用于在运行时填充 <see cref="GhostRelevancy"/> 集合
    /// 通过它声明哪些 Ghost 与给定连接相关，具体行为取决于 <see cref="GhostRelevancyMode"/>
    /// </summary>
    public struct RelevantGhostForConnection : IEquatable<RelevantGhostForConnection>, IComparable<RelevantGhostForConnection>
    {
        /// <summary>
        /// 使用给定连接 ID 和 Ghost ID 构造新实例
        /// </summary>
        /// <param name="connection">连接 ID</param>
        /// <param name="ghost">Ghost ID 值</param>
        public RelevantGhostForConnection(int connection, int ghost)
        {
            Connection = connection;
            Ghost = ghost;
        }
        /// <summary>
        /// 返回 <paramref name="other"/> 是否与当前实例相等
        /// </summary>
        /// <param name="other">要比较的实例</param>
        /// <returns>连接 ID 和 Ghost ID 是否相同</returns>
        public bool Equals(RelevantGhostForConnection other)
        {
            return Connection == other.Connection && Ghost == other.Ghost;
        }
        /// <summary>
        /// 用于排序的比较方法
        /// </summary>
        /// <param name="other">要比较的实例</param>
        /// <returns>按连接 ID 和 Ghost ID 得到的排序结果</returns>
        public int CompareTo(RelevantGhostForConnection other)
        {
            if (Connection == other.Connection)
                return Ghost - other.Ghost;
            return Connection - other.Connection;
        }
        /// <summary>
        /// 适合将 RelevantGhostForConnection 插入 HashMap 或其他键值容器的 Hash Code
        /// 保证对连接与 Ghost 的组合唯一
        /// </summary>
        /// <returns>基于连接 ID 和 Ghost ID 的 Hash Code</returns>
        public override int GetHashCode()
        {
            return (Connection << 24) | Ghost;
        }
        /// <summary>
        /// 此 Ghost 相关的连接
        /// </summary>
        public int Connection;
        /// <summary>
        /// 实体的 Ghost ID
        /// </summary>
        public int Ghost;
    }

    /// <summary>
    /// 存在于服务器上的单例组件
    /// 每帧收集应向或不应向给定客户端复制的 Ghost 集合
    /// </summary>
    /// <remarks>
    /// 使用 GhostRelevancy 避免复制玩家既看不到也无法交互的实体
    /// </remarks>
    public struct GhostRelevancy : IComponentData
    {
        internal GhostRelevancy(NativeParallelHashMap<RelevantGhostForConnection, int> set)
        {
            GhostRelevancySet = set;
            GhostRelevancyMode = GhostRelevancyMode.Disabled;
            DefaultRelevancyQuery = default;
        }
        /// <summary>
        /// 指定 <see cref="GhostRelevancySet"/> 中的 Ghost 应向客户端复制，即相关
        /// 还是不复制，即不相关
        /// </summary>
        public GhostRelevancyMode GhostRelevancyMode;
        /// <summary>
        /// 连接与 Ghost 组合的集合，用于指定当前模拟 Tick 中哪些 Ghost 应向给定连接复制
        /// 或根据 <see cref="GhostRelevancyMode"/> 不向其复制
        /// 组件类型级规则参见 <see cref="DefaultRelevancyQuery"/>
        /// </summary>
        public readonly NativeParallelHashMap<RelevantGhostForConnection, int> GhostRelevancySet;

        /// <summary>
        /// 使用此查询指定哪些 Ghost 默认相关的组件类型级规则
        /// 但 <see cref="GhostRelevancySet"/> 会覆盖此筛选结果
        /// 例如
        /// Mode = SetIsRelevant, DefaultRelevancyQuery = Any&lt;MyComponentA&gt;, GhostRelevancySet = ghostWithComponentB
        /// - 所有具有 MyComponentA 的 Ghost，加上单个 ghostWithComponentB，均为相关
        /// Mode = SetIsIrrelevant, DefaultRelevancyQuery = Any&lt;MyComponentA&gt;, GhostRelevancySet = ghostWithComponentA
        /// - 所有具有 MyComponentA 的 Ghost 均为相关，但单个 ghostWithComponentA 除外
        /// </summary>
        /// <remarks>
        /// 此查询在内部转换为 <see cref="EntityQueryMask"/>，因此筛选时适用相同限制
        /// 如果多个 Ghost 类型都应默认始终相关，请确保查询使用 Any 筛选器
        /// </remarks>
        public EntityQuery DefaultRelevancyQuery;
    }
}
