using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace Unity.NetCode
{
    /// <summary>
    /// 包含待序列化 Ghost <see cref="ArchetypeChunk"/> 的结构
    /// 每个 Chunk 都有自己的优先级，该优先级根据 Authoring 时为各 Ghost Prefab 设置的 Importance 缩放系数计算
    /// 还可以通过自定义 <see cref="GhostImportance.BatchScaleImportanceFunction"/> 进一步缩放
    /// </summary>
    public struct PrioChunk : IComparable<PrioChunk>
    {
        /// <summary>
        /// 应处理的 Ghost Chunk
        /// </summary>
        public ArchetypeChunk chunk;
        /// <summary>
        /// Chunk 的优先级，使用 <see cref="GhostImportance.BatchScaleImportanceFunction"/> 缩放时
        /// 该方法负责将此字段更新为缩放后的优先级
        /// </summary>
        public int priority;
        /// <summary>
        /// 表示整个 Chunk 相关性的快速路径字段
        /// </summary>
        /// <remarks>
        /// <para>
        /// 当相关性模式为 <see cref="GhostRelevancyMode.Disabled"/> 或 <see cref="GhostRelevancyMode.SetIsIrrelevant"/> 时默认为 <c>true</c>
        /// 否则默认为 <c>false</c>
        /// </para>
        /// <para>
        /// 使用此布尔值时，无需将 Ghost 实例写入全局 GhostRelevancySet
        /// 除非需要添加例外，例如某个 Ghost 距离玩家很远但仍应保持相关
        /// </para>
        /// <para>
        /// 注意：不能使用 <see cref="priority"/> 表示相关性，因为相关性逻辑仍要求偶尔处理该 Chunk
        /// 换言之，人为压低 Importance 可能会破坏相关性处理
        /// </para>
        /// </remarks>
        public bool isRelevant;
        /// <summary>
        /// Chunk 中应开始序列化的第一个实体索引，通常为 0
        /// 如果本次无法序列化整个 Chunk，下次会从此索引继续复制 Ghost
        /// </summary>
        internal int startIndex;
        /// <summary>
        /// <see cref="GhostCollectionPrefab"/> 中的类型索引，用于获取序列化 Ghost 所需信息
        /// </summary>
        internal int ghostType;
        /// <summary>
        /// 用于按优先级降序排序
        /// </summary>
        /// <param name="other">另一个 PrioChunk</param>
        /// <returns>降序比较结果</returns>
        public int CompareTo(PrioChunk other)
        {
            // 反转优先级比较方向以实现降序排序
            return other.priority - priority;
        }
    }
    /// <summary>
    /// 用于控制服务器 Importance 缩放，也称优先级缩放设置的单例组件
    /// <see cref="GhostSendSystem"/> 使用它确定写入各连接每个 Snapshot 的 Ghost Chunk 优先级
    /// 因此 Importance 缩放按连接分别应用
    /// 在仅服务器的用户代码系统中创建此单例即可启用该功能
    /// 延伸阅读：https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/optimizations.html#importance-scaling
    /// </summary>
    /// <remarks>
    /// Importance 缩放最常见的用例是基于距离的重要度缩放
    /// 即附近 Ghost 的更新发送频率显著高于远处 Ghost
    /// 默认实现 <see cref="GhostDistanceImportance"/> 正是采用这种方式
    /// </remarks>
    [BurstCompile]
    public struct GhostImportance : IComponentData
    {
        /// <summary>
        /// Importance 缩放委托，定义 <see cref="GhostSendSystem"/> 计算 Importance 缩放时使用的接口
        /// 此方法返回的 Importance 值越高，Ghost 数据同步越频繁
        /// 示例实现参见 <see cref="GhostDistanceImportance"/>
        /// </summary>
        /// <param name="connectionData">每个连接的数据，例如世界中应优先处理的位置</param>
        /// <param name="importanceData">可选配置数据，例如各 Tile 的配置，必须处理 IntPtr.Zero</param>
        /// <param name="chunkTile">每个 Chunk 的信息，例如实体的 Tile 索引</param>
        /// <param name="basePriority"><see cref="GhostSendSystem"/> 根据上次更新 Tick 和非相关状态计算的优先级</param>
        /// <returns>缩放后的 Importance 值</returns>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ScaleImportanceDelegate(IntPtr connectionData, IntPtr importanceData, IntPtr chunkTile, int basePriority);

        /// <summary>
        /// <see cref="ScaleImportanceDelegate"/> 的默认实现，不进行计算并直接返回 basePriority
        /// </summary>
        public static readonly PortableFunctionPointer<ScaleImportanceDelegate> NoScaleFunctionPointer =
            new PortableFunctionPointer<ScaleImportanceDelegate>(NoScale);

        /// <summary>
        /// Importance 缩放委托，定义 <see cref="GhostSendSystem"/> 计算 Importance 缩放时使用的接口
        /// 此方法负责修改所有 Chunk 的 <see cref="PrioChunk.priority"/> 属性
        /// 优先级越高，Ghost 数据同步越频繁，示例实现参见 <see cref="GhostDistanceImportance"/>
        /// </summary>
        /// <param name="connectionData">每个连接的数据，例如世界中应优先处理的位置</param>
        /// <param name="importanceData">可选配置数据，例如各 Tile 的配置，必须处理 IntPtr.Zero</param>
        /// <param name="sharedComponentTypeHandlePtr">用于获取每个 Chunk Tile 信息的 <see cref="DynamicSharedComponentTypeHandle"/>，例如各 Chunk 的 Tile 索引</param>
        /// <param name="chunkData">Chunk 数据</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void BatchScaleImportanceDelegate(IntPtr connectionData, IntPtr importanceData, IntPtr sharedComponentTypeHandlePtr,
            ref UnsafeList<PrioChunk> chunkData);
        /// <summary>
        /// <para>
        /// 将按照 <see cref="BatchScaleImportanceDelegate"/> 的说明，使用收集的数据调用此函数指针
        /// </para>
        /// <para>必须设置此函数指针或 <see cref="BatchScaleImportanceFunction"/> 中的一个
        /// 也可以同时设置两者，此时优先使用 BatchScaleImportanceFunction
        /// </para>
        /// </summary>
        [Obsolete("Prefer `BatchScaleImportanceDelegate` as it significantly reduces the total number of function pointer calls. RemoveAfter 1.x", false)]
        public PortableFunctionPointer<ScaleImportanceDelegate> ScaleImportanceFunction;
        /// <summary>
        /// <para>
        /// 将按照 <see cref="BatchScaleImportanceDelegate"/> 的说明，使用收集的数据调用此函数指针
        /// </para>
        /// <para>必须设置此函数指针或 <see cref="ScaleImportanceFunction"/> 中的一个
        /// 也可以同时设置两者，此时优先使用 BatchScaleImportanceFunction
        /// </para>
        /// </summary>
        public PortableFunctionPointer<BatchScaleImportanceDelegate> BatchScaleImportanceFunction;
        /// <summary>
        /// 连接数据的 ComponentType
        /// <see cref="GhostSendSystem"/> 会在调用 <see cref="BatchScaleImportanceFunction"/> 指向的函数前查询此组件类型
        /// </summary>
        public ComponentType GhostConnectionComponentType;
        /// <summary>
        /// 配置数据的可选单例 ComponentType
        /// 不需要时保留默认值，此时会将 <see cref="IntPtr.Zero"/> 传入 <see cref="BatchScaleImportanceFunction"/>
        /// <see cref="GhostSendSystem"/> 会查询此组件类型，并在调用 <see cref="BatchScaleImportanceFunction"/> 时传入数据
        /// </summary>
        public ComponentType GhostImportanceDataType;
        /// <summary>
        /// 每个 Chunk 数据对应的 ComponentType，必须是 Shared Component 类型
        /// 每个 Chunk 表示一组共享某个 Importance 相关值的实体，例如到玩家角色控制器的距离
        /// <see cref="GhostSendSystem"/> 会在调用 <see cref="BatchScaleImportanceFunction"/> 指向的函数前查询此组件类型
        /// </summary>
        /// <remarks>
        /// 提示：可以根据此类型是否存在，筛选或决定哪些 Ghost Chunk 需要由 <see cref="GhostSendSystem"/> 执行 Importance 缩放
        /// 如需排除某类型，不要向其 Chunk 添加此 Shared Component
        /// </remarks>
        public ComponentType GhostImportancePerChunkDataType;

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(ScaleImportanceDelegate))]
        static int NoScale(IntPtr connectionData, IntPtr importanceData, IntPtr chunkTile, int basePriority)
        {
            return basePriority;
        }

#pragma warning disable 618 // 类型或成员已过时
        /// <summary>
        /// 此属性用于成功抑制过时警告
        /// 在 <see cref="GhostSendSystem"/> 内抑制警告无效，推测与 SystemAPI 代码生成有关
        /// </summary>
        internal PortableFunctionPointer<ScaleImportanceDelegate> ScaleImportanceFunctionSuppressedWarning => ScaleImportanceFunction;
#pragma warning restore 618 // 类型或成员已过时
    }
}
