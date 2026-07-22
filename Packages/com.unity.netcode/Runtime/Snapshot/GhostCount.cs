using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 包含 Ghost 计数所需 API 和集合的单例组件
    /// </summary>
    [BurstCompile]
    public struct GhostCount : IComponentData
    {
        /// <summary>
        /// 服务器希望发送给此客户端的相关 Ghost <b>近似</b>总数
        /// 每个 Snapshot 都会更新此值，因此客户端每次收到 Snapshot 时也会更新
        /// </summary>
        /// <seealso cref="InstantiatedPercent"/>
        /// <seealso cref="ReceivedPercent"/>
        public int GhostCountOnServer => IsCreated ? m_GhostCompletionCount[0] : 0;

        /// <inheritdoc cref="GhostCountReceivedOnClient"/>
        [Obsolete("Prefer either GhostCountInstantiatedOnClient or GhostCountReceivedOnClient, as this variable is ambiguous (and maps to GhostCountReceivedOnClient). RemoveAfter 1.x.", false)]
        public int GhostCountOnClient => IsCreated ? m_GhostCompletionCount[1] : 0;

        /// <summary>
        /// 客户端实际实例化且与当前连接相关的 Ghost 总数
        /// 不统计 <see cref="PendingSpawnPlaceholder"/> Ghost 实例
        /// 每当最终化的 Ghost 通过 <see cref="GhostSpawnSystemGroup"/> 实际实例化或销毁时都会更新此计数
        /// <br/><see cref="IsCreated"/> 为 false 时值为零
        /// 可结合 <see cref="GhostCountOnServer"/> 判断客户端已接收多少状态
        /// </summary>
        /// <remarks>
        /// 注意：如果相关性集合突然变化，或服务器在单帧内销毁大量 Ghost
        /// 客户端的 Ghost 数量可能暂时超过其应有数量
        /// </remarks>
        /// <seealso cref="InstantiatedPercent"/>
        /// <seealso cref="GhostCountReceivedOnClient"/>
        public int GhostCountInstantiatedOnClient => IsCreated ? m_GhostCompletionCount[2] : 0;

        /// <summary>
        /// 客户端已接收且与当前连接相关的 Ghost 总数，并非已实例化数量
        /// 每当收到并处理 Snapshot 时都会更新此计数
        /// <br/>已接收 Ghost 数量可能与当前已生成 Ghost 数量不同
        /// <br/><see cref="IsCreated"/> 为 false 时值为零
        /// 可结合 <see cref="GhostCountOnServer"/> 判断客户端已接收多少状态
        /// </summary>
        /// <remarks>
        /// 注意：如果相关性集合突然变化，或服务器在单帧内销毁大量 Ghost
        /// 客户端的 Ghost 数量可能暂时超过其应有数量
        /// </remarks>
        /// <seealso cref="ReceivedPercent"/>
        /// <seealso cref="GhostCountInstantiatedOnClient"/>
        public int GhostCountReceivedOnClient => IsCreated ? m_GhostCompletionCount[1] : 0;

        /// <summary>
        /// 客户端已实例化 Ghost 数量 <see cref="GhostCountInstantiatedOnClient"/> 相对于
        /// 服务器声明存在的 Ghost 数量 <see cref="GhostCountOnServer"/> 的比例
        /// <br/>仅统计相关 Ghost
        /// <br/>没有预期 Ghost 时为 0%，例如服务器未生成 Ghost、没有相关 Ghost
        /// 或此结构尚未初始化，即 <see cref="IsCreated"/> 为 false
        /// 此值不同于 <see cref="ReceivedPercent"/>
        /// </summary>
        /// <remarks>
        /// 注意：如果相关性集合突然变化，或服务器在单帧内销毁大量 Ghost
        /// 客户端的 Ghost 数量可能暂时超过其应有数量，因此此值可能大于 100%
        /// <br/>还需注意，由于上述细节，Ghost 数量可能正确但集合内容不正确
        /// 换言之，此百分比只是对客户端已复制全部所需内容的粗略估计
        /// </remarks>
        public float InstantiatedPercent => IsCreated && GhostCountOnServer != 0 ? (float) GhostCountInstantiatedOnClient / GhostCountOnServer : -1;

        /// <summary>
        /// 客户端已接收 Ghost 数量 <see cref="GhostCountReceivedOnClient"/> 相对于
        /// 服务器声明存在的 Ghost 数量 <see cref="GhostCountOnServer"/> 的比例
        /// <br/>仅统计相关 Ghost
        /// <br/>没有预期 Ghost 时为 0%，例如服务器未生成 Ghost、没有相关 Ghost
        /// 或此结构尚未初始化，即 <see cref="IsCreated"/> 为 false
        /// 此值不同于 <see cref="InstantiatedPercent"/>
        /// </summary>
        /// <remarks>
        /// 注意：如果相关性集合突然变化，或服务器在单帧内销毁大量 Ghost
        /// 客户端的 Ghost 数量可能暂时超过其应有数量，因此此值可能大于 100%
        /// <br/>还需注意，由于上述细节，Ghost 数量可能正确但集合内容不正确
        /// 换言之，此百分比只是对客户端已复制全部所需内容的粗略估计
        /// </remarks>
        public float ReceivedPercent => IsCreated && GhostCountOnServer != 0 ? (float) GhostCountReceivedOnClient / GhostCountOnServer : -1;

        /// <summary>
        /// 表示这些值是否有效的辅助属性
        /// </summary>
        public bool IsCreated => m_GhostCompletionCount.IsCreated;

        internal NativeArray<int> m_GhostCompletionCount;

        /// <summary>
        /// 构造并初始化新的 GhostCount 实例
        /// </summary>
        /// <param name="ghostCompletionCount"></param>
        internal GhostCount(NativeArray<int> ghostCompletionCount)
        {
            m_GhostCompletionCount = ghostCompletionCount;
        }

        /// <summary>
        /// 用于调试和日志记录
        /// </summary>
        /// <returns>格式为 <c>GhostCount[received:GhostCountReceivedOnClient %, inst:GhostCountInstantiatedOnClient %, server:GhostCountOnServer]</c> 的日志文本</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString128Bytes ToFixedString() => IsCreated ? $"GhostCount[received:{GhostCountReceivedOnClient} {(int)(ReceivedPercent * 100)}%, inst:{GhostCountInstantiatedOnClient} {(int)(InstantiatedPercent * 100)}%, server:{GhostCountOnServer}]" : "GhostCount[default]";

        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }
}
