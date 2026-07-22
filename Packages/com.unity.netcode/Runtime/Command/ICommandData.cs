using System;
using System.Runtime.CompilerServices;
using System.Text;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// 使用 MaxPredictionStepBatchSize 时，客户端会批处理预测步骤，但输入变化通常会中断批处理
    /// 如果在 ICommandData 或 IInputComponentData 组件的输入字段上添加此特性，
    /// 该字段的变化不会中断批处理
    /// 例如，可以让鼠标视角输入的变化继续参与批处理，而开始移动的输入仍会中断批处理
    /// </summary>
    [AttributeUsage(AttributeTargets.Field|AttributeTargets.Property)]
    public class BatchPredictAttribute : Attribute
    {}

    /// <summary>
    /// <para>需要从客户端发送到服务器以控制 Entity 或其他对象的命令，通常是输入，应实现 ICommandData 接口</para>
    ///
    /// <para>如果需要持续从客户端向服务器发送数据，应优先使用 ICommandData 而不是 RPC，
    /// 因为 ICommandData 针对此用途进行了优化</para>
    ///
    /// <para>应尽量缩小此类型，因为其成本会随玩家数量和 Tick Rate 快速增长</para>
    ///
    /// <para>ICommandData 继承自 <see cref="IBufferElementData"/>，因此也可以从服务器序列化到客户端
    /// 它原生支持 <see cref="GhostComponentAttribute"/> 和 <see cref="GhostFieldAttribute"/> 特性
    /// 因而适用相同的 Buffer 规则：如果需要序列化 Command Buffer，则所有字段都必须标注
    /// <see cref="GhostFieldAttribute"/>，否则会产生代码生成错误</para>
    ///
    /// <para>但与普通 GhostComponent 不同，ICommandData Buffer 默认不会从服务器复制到所有客户端
    /// 如果没有 GhostComponentAttribute 控制序列化行为，则使用以下默认规则：</para>
    ///
    /// <para>- <see cref="GhostComponentAttribute.PrefabType"/> 设为 <see cref="GhostPrefabType.All"/>，Buffer 存在于所有 Ghost Variant 上</para>
    /// <para>- <see cref="GhostComponentAttribute.SendTypeOptimization"/> 设为 <see cref="GhostSendType.OnlyPredictedClients"/>
    /// 只有 Predicted Ghost 能接收该 Buffer，Interpolated Variant 会移除或禁用此组件</para>
    /// <para>- <see cref="GhostComponentAttribute.OwnerSendType"/> 设为 <see cref="SendToOwnerType.SendToNonOwner"/>
    /// 如果 Ghost 具有 Owner，则只发送给不拥有该 Ghost 的客户端</para>
    ///
    /// <para>通常不建议把 Ghost Owner 自己的命令发回给它，因此设置 <see cref="SendToOwnerType.SendToOwner"/>
    /// 会被报告为错误并忽略
    /// 此外，由于 ICommandData 的工作方式，设置 <see cref="GhostComponentAttribute.PrefabType"/> 属性时必须谨慎：</para>
    ///
    /// <para>- Server：虽然可以使用，但意义不大，系统会报告警告</para>
    /// <para>- Clients：服务器 Ghost 会移除 ICommandData Buffer，系统会报告警告</para>
    /// <para>- InterpolatedClient：服务器和 Predicted Ghost 会移除 ICommandData Buffer，系统会报告警告</para>
    /// <para>- Predicted：服务器和 Predicted Ghost 会移除 ICommandData Buffer，系统会报告警告</para>
    /// <para>- <b>AllPredicted：Interpolated Ghost 不具有 Command Buffer</b></para>
    /// <para>- <b>All：所有 Ghost 都具有 Command Buffer</b></para>
    /// </summary>
    public interface ICommandData : IBufferElementData
    {
        /// <summary>
        /// 命令应执行的 Tick
        /// 使用 <see cref="CommandDataUtility.AddCommandData{T}"/> 把命令加入 Buffer 前必须设置该值
        /// </summary>
        [DontSerializeForCommand]
        NetworkTick Tick { get; set; }

        /// <summary>
        /// 实现此方法以获得兼容 Burst 的输入结构体数据包转储日志
        /// 推荐格式：$"field1:{field1}, field2:{field2}";
        /// </summary>
        /// <remarks>此函数也必须兼容 Burst，否则会产生 Burst 编译错误</remarks>
        /// <returns>输入结构体的字段值</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString512Bytes ToFixedString() => "?ICD?";
    }

    /// <summary>
    /// 序列化和反序列化 <see cref="ICommandData"/> 时必须实现的接口
    /// 通常命令序列化和反序列化代码会自动生成，除非在 Command 结构体上添加
    /// <see cref="NetCodeDisableCommandCodeGenAttribute"/> 以改用手动序列化
    /// 启用手动序列化后，必须创建一个为目标类型实现 ICommandDataSerializer 的公共结构体，
    /// 并实现必要的发送和接收系统，才能发送与接收 RPC
    /// </summary>
    /// <typeparam name="T">数据类型</typeparam>
    public interface ICommandDataSerializer<T> where T: unmanaged, ICommandData
    {
        /// <summary>
        /// 把命令序列化到数据流
        /// </summary>
        /// <param name="writer"><see cref="DataStreamWriter"/> 实例</param>
        /// <param name="state"><see cref="RpcSerializerState"/> 实例，用于携带附加数据和序列化 Command 字段类型所需的访问器，尤其用于序列化 Entity</param>
        /// <param name="data">命令</param>
        void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in T data);
        /// <summary>
        /// 从数据流反序列化单条命令
        /// </summary>
        /// <param name="reader"><see cref="DataStreamWriter"/> 实例</param>
        /// <param name="state"><see cref="RpcSerializerState"/> 实例，用于携带附加数据和序列化 Command 字段类型所需的访问器，尤其用于序列化 Entity</param>
        /// <param name="data">命令</param>
        void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref T data);

        /// <summary>
        /// 使用差分压缩把命令序列化到数据流
        /// </summary>
        /// <param name="writer"><see cref="DataStreamWriter"/> 实例</param>
        /// <param name="state"><see cref="RpcSerializerState"/> 实例，用于携带附加数据和序列化 Command 字段类型所需的访问器，尤其用于序列化 Entity</param>
        /// <param name="data">命令</param>
        /// <param name="baseline">Baseline 命令</param>
        /// <param name="compressionModel">差分压缩模型</param>
        void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in T data, in T baseline, StreamCompressionModel compressionModel);

        /// <summary>
        /// 使用差分压缩从数据流反序列化单条命令
        /// </summary>
        /// <param name="reader"><see cref="DataStreamWriter"/> 实例</param>
        /// <param name="state"><see cref="RpcSerializerState"/> 实例，用于携带附加数据和序列化 Command 字段类型所需的访问器，尤其用于序列化 Entity</param>
        /// <param name="data">命令</param>
        /// <param name="baseline">Baseline 命令</param>
        /// <param name="compressionModel">差分压缩模型</param>
        void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref T data, in T baseline, StreamCompressionModel compressionModel);

        /// <summary>
        /// 通过 <see cref="CommandSendSystem{TCommandDataSerializer,TCommandData}"/> 发送命令时，用于对命令进行差分压缩
        /// </summary>
        /// <remarks>
        /// 为保持向后兼容且不引入破坏性变更，默认接口实现始终返回 1，表示存在变化
        /// 因此如果自行实现此接口，强烈建议覆盖该方法
        /// 自动生成的版本会自动使用逐字段 Change Mask
        /// </remarks>
        /// <param name="snapshot">当前值</param>
        /// <param name="baseline">前一个值或 Baseline 值</param>
        /// <returns>Change Mask，没有变化时为 0</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        uint CalculateChangeMask(in T snapshot, in T baseline) => 1u;

        /// <summary>
        /// 辅助方法
        /// </summary>
        /// <returns>用于数据包转储的短名称</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString64Bytes ToFixedString()
        {
            var fs = new FixedString64Bytes();
            fs.CopyFromTruncated(ComponentType.ReadWrite<T>().ToFixedString()); // 确保不会溢出
            return fs;
        }
    }

    /// <summary>
    /// 包含用于向 <see cref="ICommandData"/> Dynamic Buffer 添加命令和从中获取命令的工具方法
    /// </summary>
    public static class CommandDataUtility
    {
        /// <summary>
        /// 单个 Command Packet 能够发送的最大命令数量
        /// </summary>
        public const int k_CommandDataMaxSize = 64;

        /// <summary>
        /// 获取指定 Target Tick 对应的最新命令数据
        /// 例如 Command Buffer 包含 Tick 3、4、5、6 且 targetTick 为 5 时，
        /// 返回不超过目标值的最新 Tick 5
        /// 如果 Command Buffer 包含 1、2、3 且 targetTick 为 5，则返回 Tick 3
        /// </summary>
        /// <param name="commandArray">命令输入缓冲区</param>
        /// <param name="targetTick">要获取数据的 Target Tick</param>
        /// <param name="commandData">最近收到的输入</param>
        /// <typeparam name="T">Command Input Buffer 类型</typeparam>
        /// <returns>找到数据时返回 true；Buffer 中没有等于或早于 Target Tick 的数据时返回 false</returns>
        public static bool GetDataAtTick<T>(this DynamicBuffer<T> commandArray, NetworkTick targetTick, out T commandData)
            where T : unmanaged, ICommandData
        {
            if (!targetTick.IsValid)
            {
                commandData = default;
                return false;
            }
            int beforeIdx = 0;
            NetworkTick beforeTick = NetworkTick.Invalid;
            for (int i = 0; i < commandArray.Length; ++i)
            {
                var tick = commandArray[i].Tick;
                if (tick.IsValid && !tick.IsNewerThan(targetTick) &&
                    (!beforeTick.IsValid || tick.IsNewerThan(beforeTick)))
                {
                    beforeIdx = i;
                    beforeTick = tick;
                }
            }

            if (!beforeTick.IsValid)
            {
                commandData = default(T);
                return false;
            }

            commandData = commandArray[beforeIdx];
            return true;
        }

        /// <summary>
        /// 获取指定索引处输入的只读引用
        /// 必须在能够确认 Buffer 不会被修改的安全上下文中使用，否则引用会失效，无法保证读取的数据仍然有效
        /// </summary>
        /// <param name="buffer">要访问的 Buffer</param>
        /// <param name="index">要获取输入的索引</param>
        /// <typeparam name="T">Command 类型</typeparam>
        /// <returns>元素的只读引用</returns>
        public static ref readonly T GetInputAtIndex<T>(this DynamicBuffer<T> buffer, int index) where T: unmanaged, ICommandData
        {
            return ref buffer.ElementAtRO(index);
        }

        /// <summary>
        /// 把 <see cref="ICommandData"/> 实例添加到 Command 环形 Buffer
        /// Command Buffer 容量固定，系统通过 <see cref="ICommandData.Tick"/> 查找命令应放入的槽位，以保持 Buffer 有序
        /// 如果 Buffer 中已经存在相同 Tick 的命令，则覆盖该命令
        /// </summary>
        /// <remarks>确保把 <see cref="ICommandData.Tick"/> 设为 <see cref="NetworkTime.InputTargetTick"/></remarks>
        /// <typeparam name="T">Command 类型</typeparam>
        /// <param name="commandBuffer">要写入的 Buffer</param>
        /// <param name="commandData">要添加的单个输入结构体</param>
        /// <returns>替换了完全相同 Tick 的现有输入时返回 true</returns>
        public static bool AddCommandData<T>(this DynamicBuffer<T> commandBuffer, T commandData)
            where T : unmanaged, ICommandData
        {
            if (Hint.Unlikely(!commandData.Tick.IsValid))
                return false;

            var targetTick = commandData.Tick;
            int oldestIdx = 0;
            NetworkTick oldestTick = NetworkTick.Invalid;
            for (int i = 0; i < commandBuffer.Length; ++i)
            {
                var tick = commandBuffer[i].Tick;
                if (tick == targetTick)
                {
                    commandBuffer[i] = commandData;
                    return true;
                }

                if (!oldestTick.IsValid || oldestTick.IsNewerThan(tick))
                {
                    oldestIdx = i;
                    oldestTick = tick;
                }
            }

            if (commandBuffer.Length < k_CommandDataMaxSize)
                commandBuffer.Add(commandData);
            else
                commandBuffer[oldestIdx] = commandData;
            return false;
        }

        internal static FixedString64Bytes FormatBitsBytes(int sizeBits)
        {
            var bytes = (sizeBits + 7) / 8;
            return bytes <= 1 ? $"{sizeBits} bits" : $"{sizeBits} bits [{bytes} bytes]";
        }
    }
}
