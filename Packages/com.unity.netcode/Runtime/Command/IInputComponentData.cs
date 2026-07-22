using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于保存玩家输入的特殊组件数据接口
    /// </summary>
    /// <remarks>使用 NetCode 包时，这些输入会自动按 Command Data 处理，
    /// 并保存到客户端与服务器之间同步的 Buffer 中
    /// 此方式兼容预测等 NetCode 功能
    /// </remarks>
    public interface IInputComponentData : IComponentData
    {
        /// <inheritdoc cref="ICommandData.ToFixedString"/>
        [GenerateTestsForBurstCompatibility]
        public FixedString512Bytes ToFixedString() => "?InputComponentData?";
    }

    /// <summary>
    /// 可在 <see cref="IInputComponentData"/> 内使用此类型保存输入事件
    /// </summary>
    /// <remarks>使用此类型可以确保服务器恰好检测一次跳跃、扳机等单次输入事件
    /// </remarks>
    public struct InputEvent
    {
        /// <summary>
        /// 检测到新输入事件时返回 true，即最近已知 Tick 中该事件尚未设置
        /// </summary>
        public bool IsSet => Count > 0;

        /// <summary>
        /// 设置或启用当前 Tick 的输入事件
        /// </summary>
        public void Set()
        {
            Count++;
        }

        /// <summary>
        /// 跟踪当前帧是否已设置该事件
        /// </summary>
        /// <remarks>输入发送到服务器前被多次采样时，该值可能大于 1
        /// 此外，如果输入在传输前再次采样，已设置的事件不会被未设置状态（count=0）覆盖
        /// </remarks>
        public uint Count;

        /// <summary>
        /// 辅助方法
        /// </summary>
        /// <returns>'InputEvent[<see cref="Count"/>]'</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString32Bytes ToFixedString() => $"InputEvent[{Count}]";

        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }

    /// <summary>
    /// 用于通过 IInputComponentData 风格输入处理自动输入 Command Data 设置的接口
    /// 仅供代码生成器内部使用，请勿直接使用
    /// </summary>
    [Obsolete("The IInputBufferData interface has been deprecated. It was meant for internal use and any reference to it is considered an error. " +
              "Please always use ICommandData instead.", false)]
    public interface IInputBufferData : ICommandData
    {
        /// <summary>
        /// 把已保存的输入数据复制到给定输入数据指向的位置，
        /// 并用前一个 Command Buffer 数据元素中的计数值递减所有事件计数器
        /// </summary>
        /// <param name="prevInputBufferDataPtr">前一个 Tick 的 Command Data</param>
        /// <param name="inputPtr">已保存输入数据的复制目标位置</param>
        public void DecrementEventsAndAssignToInput(IntPtr prevInputBufferDataPtr, IntPtr inputPtr);
        /// <summary>
        /// 保存输入数据，并用当前 Tick 的 Command Buffer 中最后保存输入的计数器值递增所有事件计数器
        /// 参见 <see cref="InputEvent"/>
        /// </summary>
        /// <param name="lastInputBufferDataPtr">指向 Buffer 中最后一条 Command Data 的指针</param>
        /// <param name="inputPtr">指向要保存到此 Command Data 的输入数据的指针</param>
        public void IncrementEventsAndSetCurrentInputData(IntPtr lastInputBufferDataPtr, IntPtr inputPtr);
    }

     /// <summary>
     /// 仅供内部使用的辅助结构体，用于实现把 <see cref="IInputComponentData"/> 内容
     /// 复制到代码生成的 <see cref="ICommandData"/> Buffer 的系统
     /// </summary>
     /// <typeparam name="TInputBufferData">输入 Buffer 数据</typeparam>
     /// <typeparam name="TInputComponentData">输入组件数据</typeparam>
     [Obsolete("CopyInputToCommandBuffer has been deprecated. There is no replacement, being the method meant to be used only by code-generated systems.", false)]
     public partial struct CopyInputToCommandBuffer<TInputBufferData, TInputComponentData>
         where TInputBufferData : unmanaged, IInputBufferData
         where TInputComponentData : unmanaged, IInputComponentData
     {
          /// <summary>
          /// 仅供内部使用，用于简化创建把 <see cref="IInputComponentData"/> 数据复制到底层 <see cref="ICommandData"/> Buffer 的 System Job
         /// </summary>
         [Obsolete("CopyInputToBufferJob has been deprecated.", false)]
         public struct CopyInputToBufferJob
         {
              /// <summary>
              /// 实现组件复制和输入事件管理
              /// 应在 Job 的 <see cref="Unity.Jobs.IJob.Execute"/> 方法中调用
              /// </summary>
              /// <param name="chunk">数据所在 Chunk</param>
              /// <param name="orderIndex">顺序索引</param>
             public void Execute(ArchetypeChunk chunk, int orderIndex)
             {
             }
         }

          /// <summary>
          /// 通过更新全部组件类型句柄并创建新的 <see cref="CopyInputToBufferJob"/> 实例，
          /// 初始化 CopyInputToCommandBuffer
          /// </summary>
          /// <param name="state"><see cref="SystemState"/></param>
          /// <returns>新的 <see cref="CopyInputToBufferJob"/> 实例</returns>
         public CopyInputToBufferJob InitJobData(ref SystemState state)
         {
             return default;
         }

          /// <summary>
          /// 创建内部组件类型句柄，并向 SystemState 注册组件查询
          /// 该方法还会添加一项重要的隐式约束：要求至少存在一个具有 <see cref="NetworkId"/> 组件的连接，
          /// 从而只在客户端已连接服务器时运行父系统
          /// </summary>
          /// <remarks>
          /// 应在系统的 OnCreate 方法中调用
         /// </remarks>
         /// <param name="state"><see cref="SystemState"/></param>
          /// <returns>用于组件类型句柄的 Query</returns>
         public EntityQuery Create(ref SystemState state)
         {
             return default;
         }
     }

     /// <summary>
     /// 仅供内部使用的辅助结构体，用于实现把命令从 <see cref="ICommandData"/> Buffer
     /// 复制到 Entity 上 <see cref="IInputComponentData"/> 组件的系统
     /// </summary>
     /// <typeparam name="TInputBufferData">输入 Buffer 数据</typeparam>
     /// <typeparam name="TInputComponentData">输入组件数据</typeparam>
     [Obsolete("ApplyCurrentInputBufferElementToInputData has been deprecated. There is no replacement, being the method meant to be used only by code-generated systems.", false)]
     public partial struct ApplyCurrentInputBufferElementToInputData<TInputBufferData, TInputComponentData>
         where TInputBufferData : unmanaged, IInputBufferData
         where TInputComponentData : unmanaged, IInputComponentData
     {
          /// <summary>
          /// 用于实现 Job 的辅助结构体，该 Job 把命令从 <see cref="ICommandData"/> Buffer
          /// 复制到对应的 <see cref="IInputComponentData"/>
         /// </summary>
         [Obsolete("ApplyInputDataFromBufferJob has been deprecated.", false)]
         public struct ApplyInputDataFromBufferJob
         {
              /// <summary>
              /// 把当前 Server Tick 的命令复制到 Input Component
              /// 应在 Job 的 <see cref="Unity.Jobs.IJob.Execute"/> 方法中调用
              /// </summary>
              /// <param name="chunk">数据所在 Chunk</param>
              /// <param name="orderIndex">顺序索引</param>
             public void Execute(ArchetypeChunk chunk, int orderIndex)
             {
             }
         }

          /// <summary>
          /// 更新组件类型句柄，并创建可传递给 Job 的新 <see cref="ApplyInputDataFromBufferJob"/>
          /// </summary>
          /// <param name="state"><see cref="SystemState"/></param>
          /// <returns>新的 <see cref="ApplyInputDataFromBufferJob"/> 实例</returns>
         public ApplyInputDataFromBufferJob InitJobData(ref SystemState state)
         {
             return default;
         }
     }

     /// <summary>
    /// 用于保存 <see cref="IInputComponentData"/> 的底层 <see cref="ICommandData"/> Buffer
    /// </summary>
    /// <remarks>
    /// 无法按 Prefab 覆盖 Buffer 的复制行为，默认情况下也会为 Child Entity 发送该 Buffer
    /// </remarks>
    /// <typeparam name="T">实现 <see cref="IInputComponentData"/> 接口的 Unmanaged 结构体</typeparam>
    [DontSupportPrefabOverrides]
    [GhostComponent(SendDataForChildEntity = true)]
    [InternalBufferCapacity(0)]
    public struct InputBufferData<T> : ICommandData where T: unmanaged, IInputComponentData
    {
        /// <summary>
        /// 命令应执行的 Tick
        /// 使用 <see cref="CommandDataUtility.AddCommandData{T}"/> 把命令加入 Buffer 前必须设置该值
        /// </summary>
        [DontSerializeForCommand]
        public NetworkTick Tick { get; set; }
        /// <summary>
        /// 保存输入数据的 <see cref="IInputComponentData"/> 结构体
        /// </summary>
        public T InternalInput;

        /// <summary>
        /// 辅助方法
        /// </summary>
        /// <remarks>应优先使用 <see cref="ToPrettyFixedString"/>，因为它提供的信息更完整</remarks>
        /// <returns>仅返回 <see cref="InternalInput"/> 的 <see cref="ICommandData.ToFixedString"/> 结果</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString512Bytes ToFixedString() => InternalInput.ToFixedString();

        /// <summary>
        /// 辅助方法
        /// </summary>
        /// <returns>此结构体的完整调试信息，包括类型、Tick 和 <see cref="ICommandData.ToFixedString"/> 结果</returns>
        public FixedString4096Bytes ToPrettyFixedString() => $"IBD<{default(ICommandDataSerializer<InputBufferData<T>>).ToFixedString()}>[{Tick.ToFixedString()}|{InternalInput.ToFixedString()}]";

        /// <inheritdoc cref="ToPrettyFixedString"/>
        public override string ToString() => ToPrettyFixedString().ToString();
    }

    /// <summary>
    /// 仅供内部使用的接口，由代码生成的辅助类型实现
    /// 在与底层 <see cref="InputBufferData{T}"/> 互相复制时，用于递增和递减 <see cref="IInputComponentData"/> 事件
    /// </summary>
    /// <typeparam name="T">Input Component 类型</typeparam>
    public interface IInputEventHelper<T> where T: unmanaged, IInputComponentData
    {
        /// <summary>
        /// 把已保存的输入数据复制到给定输入数据，并用前一个 Command Buffer 数据元素中的计数值
        /// 递减所有事件计数器
        /// </summary>
        /// <param name="prevInputData">前一个 Tick 的 Command Data</param>
        /// <param name="inputData">已保存输入数据的复制目标</param>
        public void DecrementEvents(ref T inputData, in T prevInputData);
        /// <summary>
        /// 保存输入数据，并用当前 Tick 的 Command Buffer 中最后保存输入的计数器值递增所有事件计数器
        /// 参见 <see cref="InputEvent"/>
        /// </summary>
        /// <param name="lastInputData">Buffer 中最后一条 Command Data</param>
        /// <param name="inputData">要保存到此 Command Data 的输入数据</param>
        public void IncrementEvents(ref T inputData,  in T lastInputData);
    }
}
