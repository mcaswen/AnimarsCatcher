using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>
    /// 位于 <see cref="PredictedSimulationSystemGroup"/> 开始位置的 <see cref="EntityCommandBufferSystem"/>
    /// 根据网络状况和客户端接收服务器数据包的频率，此 Command Buffer 在客户端每帧可能更新多次
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 如果客户端没有预测 Ghost Entity，该 System 可能不会更新
    /// 待处理 Command 可能延后到生成新的预测 Ghost Entity 时才执行 <br/>
    ///
    /// 待处理 Command 仅在以下情况下会于加入 Buffer 的同一帧执行：<br/>
    /// - Command 在 <see cref="PredictedSimulationSystemGroup"/> 更新前入队<br/>
    /// - Command 由 <see cref="PredictedSimulationSystemGroup"/> 内执行的 System 或 Job 入队，且该组还会再运行至少一个完整或部分 Tick <br/>
    /// 对于后一种情况，以固定 Tick Rate 运行的应用不会满足该条件，例如服务器或启用垂直同步的客户端
    /// 因而所有 Command 都会延迟一个 Tick
    /// </para>
    /// <para>
    /// 通常应优先使用 <see cref="EndPredictedSimulationEntityCommandBufferSystem"/> 将期望在预测组更新结束前
    /// 或当前帧内执行的操作入队，例如：
    /// <list type="bullet">
    /// <item>在客户端或服务器生成 Entity，包括预测生成</item>
    /// <item>移除或添加 Component</item>
    /// </list>
    /// </para>
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst = true)]
    public partial class BeginPredictedSimulationEntityCommandBufferSystem : EntityCommandBufferSystem
    {
        /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton"/>
        public unsafe struct Singleton : IComponentData, IECBSingleton
        {
            internal UnsafeList<EntityCommandBuffer>* pendingBuffers;
            internal AllocatorManager.AllocatorHandle allocator;

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.CreateCommandBuffer"/>
            public EntityCommandBuffer CreateCommandBuffer(WorldUnmanaged world)
            {
                return EntityCommandBufferSystem.CreateCommandBuffer(ref *pendingBuffers, allocator, world);
            }

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.SetPendingBufferList"/>
            public void SetPendingBufferList(ref UnsafeList<EntityCommandBuffer> buffers)
            {
                pendingBuffers = (UnsafeList<EntityCommandBuffer>*)UnsafeUtility.AddressOf(ref buffers);
            }

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.SetAllocator"/>
            public void SetAllocator(Allocator allocatorIn)
            {
                allocator = allocatorIn;
            }

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.SetAllocator"/>
            public void SetAllocator(AllocatorManager.AllocatorHandle allocatorIn)
            {
                allocator = allocatorIn;
            }
        }
        /// <inheritdoc cref="EntityCommandBufferSystem.OnCreate"/>
        protected override void OnCreate()
        {
            base.OnCreate();
            this.RegisterSingleton<Singleton>(ref PendingBuffers, World.Unmanaged);
        }
    }

    /// <summary>
    /// <para>
    /// 位于 <see cref="PredictedSimulationSystemGroup"/> 结束位置的 <see cref="EntityCommandBufferSystem"/>
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 涉及预测 Ghost 的生成操作应优先通过该 System 入队，尤其是客户端预测生成
    /// 如果遵循常规生成规则，即 NetworkTime.IsFirstTimePredictedTick 为 true
    /// 便可保证它们在生成 Tick 以正确状态完成初始化，并且不会处于部分 Tick
    /// </para>
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderLast = true)]
    public partial class EndPredictedSimulationEntityCommandBufferSystem : EntityCommandBufferSystem
    {
        /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton"/>
        public unsafe struct Singleton : IComponentData, IECBSingleton
        {
            internal UnsafeList<EntityCommandBuffer>* pendingBuffers;
            internal AllocatorManager.AllocatorHandle allocator;

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.CreateCommandBuffer"/>
            public EntityCommandBuffer CreateCommandBuffer(WorldUnmanaged world)
            {
                return EntityCommandBufferSystem.CreateCommandBuffer(ref *pendingBuffers, allocator, world);
            }

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.SetPendingBufferList"/>
            public void SetPendingBufferList(ref UnsafeList<EntityCommandBuffer> buffers)
            {
                pendingBuffers = (UnsafeList<EntityCommandBuffer>*)UnsafeUtility.AddressOf(ref buffers);
            }

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.SetAllocator"/>
            public void SetAllocator(Allocator allocatorIn)
            {
                allocator = allocatorIn;
            }

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.SetAllocator"/>
            public void SetAllocator(AllocatorManager.AllocatorHandle allocatorIn)
            {
                allocator = allocatorIn;
            }
        }
        /// <inheritdoc cref="EntityCommandBufferSystem.OnCreate"/>
        protected override void OnCreate()
        {
            base.OnCreate();
            this.RegisterSingleton<Singleton>(ref PendingBuffers, World.Unmanaged);
        }
    }
}
