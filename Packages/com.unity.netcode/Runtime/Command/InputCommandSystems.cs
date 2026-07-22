using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.UIElements;

namespace Unity.NetCode
{
    /// <summary>
    /// 仅供内部使用且不应直接调用的 Job
    /// 用于把实现 <see cref="IInputComponentData"/> 的结构体输入数据复制到底层
    /// <see cref="InputBufferData{T}"/> 命令数据 Buffer
    /// 如果 Input Component 包含输入事件，该 Job 还负责递增 <see cref="InputEvent"/> 计数器
    /// </summary>
    /// <typeparam name="TInputComponentData">输入组件数据</typeparam>
    /// <typeparam name="TInputHelper">输入辅助类型</typeparam>
    [BurstCompile]
    public struct CopyInputToBufferJob<TInputComponentData, TInputHelper> : IJobChunk
        where TInputComponentData : unmanaged, IInputComponentData
        where TInputHelper : unmanaged, IInputEventHelper<TInputComponentData>
    {
        internal NetworkTick InputTargetTick;
        internal int ConnectionId;
        [ReadOnly] internal ComponentTypeHandle<TInputComponentData> InputDataType;
        [ReadOnly] internal ComponentTypeHandle<GhostOwner> GhostOwnerDataType;
        internal BufferTypeHandle<InputBufferData<TInputComponentData>> InputBufferDataType;

        /// <summary>
        /// 把当前 Server Tick 的 Input Component 复制到 Command Buffer
        /// </summary>
        /// <param name="chunk">数据所在 Chunk</param>
        /// <param name="unfilteredChunkIndex">未过滤的 Chunk 索引</param>
        /// <param name="useEnabledMask">是否使用 Enabled Mask</param>
        /// <param name="chunkEnabledMask">Chunk 启用掩码</param>
        [BurstCompile]
        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var inputs = chunk.GetNativeArray(ref InputDataType);
            var owners = chunk.GetNativeArray(ref GhostOwnerDataType);
            var inputBuffers = chunk.GetBufferAccessor(ref InputBufferDataType);
            var helper = new TInputHelper();
            for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; ++i)
            {
                var inputData = inputs[i];
                var owner = owners[i];
                var inputBuffer = inputBuffers[i];

                // 验证 Owner ID，避免预测全部 Entity 时采集到非本地玩家的输入
                if (owner.NetworkId != ConnectionId)
                    continue;
                // 此逻辑能够工作的原因是 Tick 切换到新值时，GetDataAtTick 会返回前一个 Tick 的值
                // 因此该方法始终以当前事件计数器为基础，相对前一个 Tick 递增，满足计数器只能递增的要求

                inputBuffer.GetDataAtTick(InputTargetTick, out var lastInputDataElement);
                // 递增当前 Tick 的事件计数
                // 同一预测或模拟 Tick 内可能先有事件后无事件，此逻辑仍会把它记录为事件（count > 0），
                // 避免后一次采样把事件覆盖为 0 或 false
                var currentInput = default(InputBufferData<TInputComponentData>);
                currentInput.Tick = InputTargetTick;
                currentInput.InternalInput = inputData;
                helper.IncrementEvents(ref currentInput.InternalInput, lastInputDataElement.InternalInput);

                inputBuffer.AddCommandData(currentInput);
            }
        }
    }

    /// <summary>
    /// 仅供内部使用的系统，把 <see cref="IInputComponentData"/> 内容复制到 Entity 上的
    /// <see cref="InputBufferData{T}"/> 缓冲区
    /// </summary>
    /// <typeparam name="TInputComponentData">输入组件数据</typeparam>
    /// <typeparam name="TInputHelper">输入辅助类型</typeparam>
    [BurstCompile]
    [UpdateInGroup(typeof(CopyInputToCommandBufferSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    public partial struct CopyInputToCommandBufferSystem<TInputComponentData, TInputHelper> : ISystem
        where TInputComponentData : unmanaged, IInputComponentData
        where TInputHelper : unmanaged, IInputEventHelper<TInputComponentData>
    {
        private EntityQuery m_EntityQuery;
        private EntityQuery m_TimeQuery;
        private EntityQuery m_ConnectionQuery;
        private ComponentTypeHandle<GhostOwner> m_GhostOwnerDataType;
        private ComponentTypeHandle<TInputComponentData> m_InputDataType;
        private BufferTypeHandle<InputBufferData<TInputComponentData>> m_InputBufferTypeHandle;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<InputBufferData<TInputComponentData>, TInputComponentData, GhostOwner>();
            m_EntityQuery = state.GetEntityQuery(builder);
            m_TimeQuery = state.GetEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            m_ConnectionQuery = state.GetEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.ReadOnly<LocalConnection>());
            m_GhostOwnerDataType = state.GetComponentTypeHandle<GhostOwner>(true);
            m_InputBufferTypeHandle = state.GetBufferTypeHandle<InputBufferData<TInputComponentData>>();
            m_InputDataType = state.GetComponentTypeHandle<TInputComponentData>(true);
            state.RequireForUpdate<NetworkId>();
            state.RequireForUpdate(m_EntityQuery);
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            m_GhostOwnerDataType.Update(ref state);
            m_InputBufferTypeHandle.Update(ref state);
            m_InputDataType.Update(ref state);

            var job = new CopyInputToBufferJob<TInputComponentData, TInputHelper>
            {
                InputTargetTick =  m_TimeQuery.GetSingleton<NetworkTime>().InputTargetTick,
                ConnectionId = m_ConnectionQuery.GetSingleton<NetworkId>().Value,
                GhostOwnerDataType = m_GhostOwnerDataType,
                InputBufferDataType = m_InputBufferTypeHandle,
                InputDataType = m_InputDataType,
            };
            state.Dependency = job.Schedule(m_EntityQuery, state.Dependency);
        }
    }

    /// <summary>
    /// 仅供内部使用
    /// 使用 Forced Input Latency（<see cref="ClientTickRate.ForcedInputLatencyTicks"/>）时，
    /// 此系统会在采集输入前把最新输入写回 <see cref="IInputComponentData"/>
    /// 这会把输入结构体恢复到正确状态，以便由用户的输入采集步骤更新，
    /// 从而让鼠标俯仰角和偏航角等增量值正确累加
    /// </summary>
    /// <typeparam name="TInputComponentData">输入组件数据</typeparam>
    /// <typeparam name="TInputHelper">输入辅助类型</typeparam>
    [BurstCompile]
    [UpdateInGroup(typeof(GhostInputSystemGroup), OrderFirst = true)]
    public partial struct ApplyCurrentInputBufferElementToInputDataForGatherSystem<TInputComponentData, TInputHelper> : ISystem
        where TInputComponentData : unmanaged, IInputComponentData
        where TInputHelper : unmanaged, IInputEventHelper<TInputComponentData>
    {
        private EntityQuery m_EntityQuery;
        private EntityQuery m_TimeQuery;
        private EntityTypeHandle m_EntityTypeHandle;
        private ComponentTypeHandle<TInputComponentData> m_InputDataType;
        private BufferTypeHandle<InputBufferData<TInputComponentData>> m_InputBufferTypeHandle;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<InputBufferData<TInputComponentData>, TInputComponentData, PredictedGhost>();
            m_EntityQuery = state.GetEntityQuery(builder);
            m_TimeQuery = state.GetEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_InputBufferTypeHandle = state.GetBufferTypeHandle<InputBufferData<TInputComponentData>>();
            m_InputDataType = state.GetComponentTypeHandle<TInputComponentData>();
            state.RequireForUpdate<NetworkId>();
            state.RequireForUpdate(m_EntityQuery);
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var networkTime = m_TimeQuery.GetSingleton<NetworkTime>();
            if (networkTime.EffectiveInputLatencyTicks == 0)
                return;

            m_EntityTypeHandle.Update(ref state);
            m_InputBufferTypeHandle.Update(ref state);
            m_InputDataType.Update(ref state);
            var jobData = new ApplyInputDataFromBufferJob<TInputComponentData, TInputHelper>
            {
                CurrentPredictionTick = networkTime.InputTargetTick, // 注意此处使用 `InputTargetTick`
                StepLength = networkTime.SimulationStepBatchSize,
                InputBufferTypeHandle = m_InputBufferTypeHandle,
                InputDataType = m_InputDataType
            };
            state.Dependency = jobData.Schedule(m_EntityQuery, state.Dependency);
        }
    }

    /// <summary>
    /// 仅供内部使用的系统，把命令从 <see cref="InputBufferData{T}"/> Buffer
    /// 复制到 Entity 上的 <see cref="IInputComponentData"/> 组件
    /// </summary>
    /// <remarks>
    /// 此系统需要尽早运行，以确保输入处理系统运行前，已经把输入数据从 Buffer 应用到输入数据结构体
    /// </remarks>
    /// <typeparam name="TInputComponentData">输入组件数据</typeparam>
    /// <typeparam name="TInputHelper">输入辅助类型</typeparam>
    [BurstCompile]
    [UpdateInGroup(typeof(CopyCommandBufferToInputSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(PredictedFixedStepSimulationSystemGroup))]
    public partial struct ApplyCurrentInputBufferElementToInputDataSystem<TInputComponentData, TInputHelper> : ISystem
        where TInputComponentData : unmanaged, IInputComponentData
        where TInputHelper : unmanaged, IInputEventHelper<TInputComponentData>
    {
        private EntityQuery m_EntityQuery;
        private EntityQuery m_TimeQuery;
        private EntityTypeHandle m_EntityTypeHandle;
        private ComponentTypeHandle<TInputComponentData> m_InputDataType;
        private BufferTypeHandle<InputBufferData<TInputComponentData>> m_InputBufferTypeHandle;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<InputBufferData<TInputComponentData>, TInputComponentData, PredictedGhost>();
            m_EntityQuery = state.GetEntityQuery(builder);
            m_TimeQuery = state.GetEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_InputBufferTypeHandle = state.GetBufferTypeHandle<InputBufferData<TInputComponentData>>();
            m_InputDataType = state.GetComponentTypeHandle<TInputComponentData>();
            state.RequireForUpdate<NetworkId>();
            state.RequireForUpdate(m_EntityQuery);
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            m_EntityTypeHandle.Update(ref state);
            m_InputBufferTypeHandle.Update(ref state);
            m_InputDataType.Update(ref state);

            var networkTime = m_TimeQuery.GetSingleton<NetworkTime>();
            var jobData = new ApplyInputDataFromBufferJob<TInputComponentData, TInputHelper>
            {
                CurrentPredictionTick = networkTime.ServerTick,
                StepLength = networkTime.SimulationStepBatchSize,
                InputBufferTypeHandle = m_InputBufferTypeHandle,
                InputDataType = m_InputDataType,
            };
            state.Dependency = jobData.Schedule(m_EntityQuery, state.Dependency);
        }
    }

    /// <summary>
    /// 仅供内部使用且不应直接调用的 Job，在预测循环内运行
    /// 它把当前模拟 Tick 的输入数据从 <see cref="InputBufferData{T}"/> Command Buffer
    /// 复制到 <see cref="IInputComponentData"/> 组件
    /// 该 Job 负责重新计算所有 <see cref="InputEvent"/> 计数，确保从上一个 Tick 或批次以来发生的事件
    /// 被正确报告为已设置，另请参阅 <see cref="NetworkTime.SimulationStepBatchSize"/> 和 <see cref="InputEvent.IsSet"/>
    /// </summary>
    /// <typeparam name="TInputComponentData">输入组件数据</typeparam>
    /// <typeparam name="TInputHelper">输入辅助类型</typeparam>
    [BurstCompile]
    public struct ApplyInputDataFromBufferJob<TInputComponentData, TInputHelper> : IJobChunk
        where TInputComponentData : unmanaged, IInputComponentData
        where TInputHelper : unmanaged, IInputEventHelper<TInputComponentData>
    {
        internal NetworkTick CurrentPredictionTick;
        internal int StepLength;
        internal ComponentTypeHandle<TInputComponentData> InputDataType;
        internal BufferTypeHandle<InputBufferData<TInputComponentData>> InputBufferTypeHandle;

        /// <summary>
        /// 把当前 Server Tick 的命令复制到 Input Component
        /// </summary>
        /// <param name="chunk">数据所在 Chunk</param>
        /// <param name="unfilteredChunkIndex">未过滤的 Chunk 索引</param>
        /// <param name="useEnabledMask">是否使用 Enabled Mask</param>
        /// <param name="chunkEnabledMask">Chunk 启用掩码</param>
        [BurstCompile]
        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            // 单 World Host 仍需复制 Buffer，因为采样输入时仍然需要 InputEvent
            var inputs = chunk.GetNativeArray(ref InputDataType);
            var inputBuffers = chunk.GetBufferAccessor(ref InputBufferTypeHandle);
            var helper = default(TInputHelper);
            for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; ++i)
            {
                var inputBuffer = inputBuffers[i];
                inputBuffer.GetDataAtTick(CurrentPredictionTick, out var inputDataElement);
                // 对当前 Tick 和 Tick-StepLength 进行采样
                // 如果 Buffer 中不存在该 Tick，就返回距离它最近的最新输入，并为 Tick-StepLength 返回相同输入
                // 这是正确结果，因为此时应假定同一 Tick 的输入正在重复
                var prevSampledTick = CurrentPredictionTick;
                prevSampledTick.Subtract((uint)StepLength);
                inputBuffer.GetDataAtTick(prevSampledTick, out var prevInputDataElement);
                // 重置输入数据以匹配当前输入，并递减事件计数
                var inputData = inputDataElement.InternalInput;
                helper.DecrementEvents(ref inputData, prevInputDataElement.InternalInput);
                inputs[i] = inputData;
            }
        }
    }
}
