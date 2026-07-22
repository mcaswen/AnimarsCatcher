using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;
using Unity.Profiling;

namespace Unity.NetCode
{
    /// <summary>
    /// 仅存在于客户端已生成 Ghost 上的 Component
    /// 用于记录存放服务器传入 Ghost Snapshot 的最新 <see cref="SnapshotDataBuffer"/> 历史槽位
    /// </summary>
    public struct SnapshotData : IComponentData
    {
        /// <summary>
        /// 仅供内部使用
        /// </summary>
        public struct DataAtTick
        {
            /// <summary>
            /// 指向 Tick 小于或等于目标 Tick 的 Snapshot 数据
            /// </summary>
            public System.IntPtr SnapshotBefore;
            /// <summary>
            /// 指向 Tick 比目标 Tick 更新的 Snapshot 数据
            /// </summary>
            public System.IntPtr SnapshotAfter;
            /// <summary>
            /// 对插值 Ghost 的 Component 字段进行插值或外推时使用的当前比例
            /// </summary>
            public float InterpolationFactor;
            /// <summary>
            /// 当前正在更新或反序列化的目标服务器 Tick
            /// </summary>
            public NetworkTick Tick;
            /// <summary>
            /// 包含早于目标 <see cref="Tick"/> 的 Ghost Snapshot 的历史槽位索引
            /// </summary>
            public int BeforeIdx;
            /// <summary>
            /// 包含晚于目标 <see cref="Tick"/> 的 Ghost Snapshot 的历史槽位索引
            /// </summary>
            public int AfterIdx;
            /// <summary>
            /// 发送 Component 时 <see cref="GhostComponentAttribute.OwnerSendType"/> 属性必须满足的掩码
            /// 该掩码取决于 <see cref="GhostOwner"/> Component 是否存在及其值：
            /// Entity 上不存在 <see cref="GhostOwner"/> 时为 <see cref="SendToOwnerType.All"/>
            /// <see cref="GhostOwner"/> 等于客户端 <see cref="NetworkId"/> 时为 <see cref="SendToOwnerType.SendToOwner"/>
            /// <see cref="GhostOwner"/> 不等于客户端 <see cref="NetworkId"/> 时为 <see cref="SendToOwnerType.SendToNonOwner"/>
            /// </summary>
            public SendToOwnerType RequiredOwnerSendMask;
            /// <summary>
            /// 拥有该 Ghost 的客户端 NetworkId
            /// Ghost 没有 <see cref="NetCode.GhostOwner"/> 时为 0
            /// </summary>
            public int GhostOwner;
        }
        /// <summary>
        /// Ghost Snapshot 的字节大小
        /// Ghost Entity 生成后保持不变，对应 <see cref="GhostCollectionPrefabSerializer.SnapshotSize"/>
        /// </summary>
        public int SnapshotSize;
        /// <summary>
        /// 存放最近一次从服务器收到的数据的历史槽位
        /// 其值始终小于 <see cref="GhostSystemConstants.SnapshotHistorySize"/>
        /// </summary>
        public int LatestIndex;

        /// <summary>
        /// 客户端最近收到的 Snapshot 服务器 Tick
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns>Buffer 非空时返回有效 Tick，否则返回无效 Tick</returns>
        readonly internal unsafe NetworkTick GetLatestTick(in DynamicBuffer<SnapshotDataBuffer> buffer)
        {
            if (buffer.Length == 0)
                return NetworkTick.Invalid;
            byte* snapshotData;
            snapshotData = (byte*)buffer.GetUnsafeReadOnlyPtr() + LatestIndex * SnapshotSize;
            return new NetworkTick{SerializedData = *(uint*)snapshotData};
        }
        /// <summary>
        /// 客户端收到的最旧 Snapshot Tick
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns>Buffer 非空时返回有效 Tick，否则返回无效 Tick</returns>
        readonly internal unsafe NetworkTick GetOldestTick(in DynamicBuffer<SnapshotDataBuffer> buffer)
        {
            if (buffer.Length == 0)
                return NetworkTick.Invalid;
            byte* snapshotData;

            // Snapshot 存储是 Ring Buffer，填满后 latest 后面的条目最旧，也就是下一个将被覆盖的条目
            // 该条目也可能尚未初始化，因此从这里向前扫描直到找到有效条目
            var oldestIndex = (LatestIndex + 1) % GhostSystemConstants.SnapshotHistorySize;
            while (oldestIndex != LatestIndex)
            {
                snapshotData = (byte*)buffer.GetUnsafeReadOnlyPtr() + oldestIndex * SnapshotSize;
                var oldestTick = new NetworkTick{SerializedData = *(uint*)snapshotData};
                if (oldestTick.IsValid)
                    return oldestTick;
                oldestIndex = (oldestIndex + 1) % GhostSystemConstants.SnapshotHistorySize;
            }

            snapshotData = (byte*)buffer.GetUnsafeReadOnlyPtr() + LatestIndex * SnapshotSize;
            return new NetworkTick{SerializedData = *(uint*)snapshotData};
        }
        /// <summary>
        /// 返回从 LatestIndex 向前回退 reverseOffset 个位置的 Snapshot 索引
        /// 传入 0 返回 LatestIndex，传入 12 返回 LatestIndex - 12，并在索引为负时正确回绕
        /// </summary>
        /// <param name="reverseOffset"></param>
        /// <returns>从 LatestIndex 回退 reverseOffset 个位置后的 Snapshot 索引，参数越界时返回 LatestIndex</returns>
        internal unsafe int GetPreviousSnapshotIndexAtOffset(int reverseOffset)
        {
            if (reverseOffset > GhostSystemConstants.SnapshotHistorySize)
                return LatestIndex;

            var previousIndex = (LatestIndex - reverseOffset);
            if (previousIndex < 0)
            {
                previousIndex += GhostSystemConstants.SnapshotHistorySize;
            }
            return previousIndex;
        }

        /// <summary>
        /// 尝试查找给定 <paramref name="targetTick"/> 附近最近的两个已接收 Ghost Snapshot
        /// 并据此填充 <paramref name="data"/>
        /// </summary>
        /// <param name="targetTick"></param>
        /// <param name="predictionOwnerOffset"></param>
        /// <param name="localNetworkId"></param>
        /// <param name="targetTickFraction"></param>
        /// <param name="buffer"></param>
        /// <param name="data"></param>
        /// <param name="MaxExtrapolationTicks"></param>
        /// <returns>至少收到一个 Snapshot 且其 Tick 小于或等于当前目标 Tick 时返回 true</returns>
        internal unsafe bool GetDataAtTick(NetworkTick targetTick, int predictionOwnerOffset,
            int localNetworkId, float targetTickFraction, in DynamicBuffer<SnapshotDataBuffer> buffer,
            out DataAtTick data, uint MaxExtrapolationTicks)
        {
            data = default;
            if (buffer.Length == 0)
                return false;
            var numBuffers = buffer.Length / SnapshotSize;
            int beforeIdx = 0;
            NetworkTick beforeTick = NetworkTick.Invalid;
            int afterIdx = 0;
            NetworkTick afterTick = NetworkTick.Invalid;
            // 如果最后一个 Tick 是部分 Tick，Before 不应包含目标 Tick，而应将目标 Tick 归入 After
            if (targetTickFraction < 1)
                targetTick.Decrement();
            // 从最新可用 Snapshot 遍历到最旧可用 Snapshot
            int slot;
            var bufferData = (byte*)buffer.GetUnsafeReadOnlyPtr();
            for (slot = 0; slot < numBuffers; ++slot)
            {
                var curIndex = (LatestIndex + GhostSystemConstants.SnapshotHistorySize - slot) % GhostSystemConstants.SnapshotHistorySize;
                var snapshotData = bufferData + curIndex * SnapshotSize;
                var tick = new NetworkTick{SerializedData = *(uint*)snapshotData};
                //var tick = new NetworkTick{SerializedData = Ticks[curIndex]};
                if (!tick.IsValid)
                    continue;
                if (tick.IsNewerThan(targetTick))
                {
                    afterTick = tick;
                    afterIdx = curIndex;
                }
                else
                {
                    beforeTick = tick;
                    beforeIdx = curIndex;
                    break;
                }
            }
            if (!beforeTick.IsValid)
            {
                return false;
            }
            data.SnapshotBefore = (System.IntPtr)(bufferData + beforeIdx * SnapshotSize);
            data.Tick = beforeTick;
            data.GhostOwner = predictionOwnerOffset != 0 ? *(int*) (data.SnapshotBefore + predictionOwnerOffset) : 0;
            if (predictionOwnerOffset == 0)
                data.RequiredOwnerSendMask = SendToOwnerType.All;
            else if (localNetworkId == data.GhostOwner)
                data.RequiredOwnerSendMask = SendToOwnerType.SendToOwner;
            else
                data.RequiredOwnerSendMask = SendToOwnerType.SendToNonOwner;
            if (!afterTick.IsValid)
            {
                data.BeforeIdx = beforeIdx;
                var beforeBeforeTick = NetworkTick.Invalid;
                int beforeBeforeIdx = 0;
                if (beforeTick != targetTick || targetTickFraction < 1)
                {
                    for (++slot; slot < numBuffers; ++slot)
                    {
                        var curIndex = (LatestIndex + GhostSystemConstants.SnapshotHistorySize - slot) % GhostSystemConstants.SnapshotHistorySize;
                        var snapshotData = bufferData + curIndex * SnapshotSize;
                        var tick = new NetworkTick{SerializedData = *(uint*)snapshotData};
                        //var tick = new NetworkTick{SerializedData = Ticks[curIndex]};
                        if (!tick.IsValid)
                            continue;
                        beforeBeforeTick = tick;
                        beforeBeforeIdx = curIndex;
                        break;
                    }
                }
                if (beforeBeforeTick.IsValid)
                {
                    data.AfterIdx = beforeBeforeIdx;
                    data.SnapshotAfter = (System.IntPtr)(bufferData + beforeBeforeIdx * SnapshotSize);

                    if (targetTick.TicksSince(beforeTick) > MaxExtrapolationTicks)
                    {
                        targetTick = beforeTick;
                        targetTick.Add(MaxExtrapolationTicks);
                    }
                    data.InterpolationFactor = (float) (targetTick.TicksSince(beforeBeforeTick)) / (float) (beforeTick.TicksSince(beforeBeforeTick));
                    if (targetTickFraction < 1)
                        data.InterpolationFactor += targetTickFraction / (float) (beforeTick.TicksSince(beforeBeforeTick));
                    data.InterpolationFactor = 1-data.InterpolationFactor;
                }
                else
                {
                    data.AfterIdx = beforeIdx;
                    data.SnapshotAfter = data.SnapshotBefore;
                    data.InterpolationFactor = 0;
                }
            }
            else
            {
                data.BeforeIdx = beforeIdx;
                data.AfterIdx = afterIdx;
                data.SnapshotAfter = (System.IntPtr)(bufferData + afterIdx * SnapshotSize);
                data.InterpolationFactor = (float) (targetTick.TicksSince(beforeTick)) / (float) (afterTick.TicksSince(beforeTick));
                if (targetTickFraction < 1)
                    data.InterpolationFactor += targetTickFraction / (float) (afterTick.TicksSince(beforeTick));
            }
            return true;
        }
    }

    /// <summary>
    /// 用于存储 Ghost Snapshot Buffer 数据内容的结构
    /// 每个 Entity 通常约占 1 到 12 KB，因此始终在堆上分配
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct SnapshotDataBuffer : IBufferElementData
    {
        /// <summary>
        /// 单个元素值
        /// </summary>
        public byte Value;
    }

    /// <summary>
    /// 用于存储 Ghost 动态 Buffer 数据内容的结构
    /// 数组一，长度为 SnapshotHistorySize
    /// uint dataSize，记录每个槽位当前的序列化数据长度并按 16 字节对齐，供差值压缩使用
    /// 数组一结束
    /// 数组二，长度为 SnapshotHistorySize
    /// 每个 Buffer 包含：
    ///     uint[maskBits] 元素变更位掩码
    ///     byte[numElements] 序列化后的 Buffer 数据
    /// 数组二结束
    /// Buffer 会按需扩容以容纳新数据
    /// 所有槽位大小相同，并且通常大于实际数据大小
    /// 序列化元素大小按 16 字节边界对齐
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct SnapshotDynamicDataBuffer : IBufferElementData
    {
        /// <summary>
        /// 单个元素值
        /// </summary>
        public byte Value;
    }

    /// <summary>
    /// 管理 Ghost Buffer 数据的辅助类型，仅供内部使用
    /// </summary>
    public unsafe struct SnapshotDynamicBuffersHelper
    {
        /// <summary>
        /// 获取动态 Snapshot Buffer 起始处 Header 的大小
        /// Header 大小固定不变
        /// </summary>
        /// <returns>动态 Snapshot Buffer 起始处 Header 的大小</returns>
        public static uint GetHeaderSize()
        {
            return (uint)GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) * GhostSystemConstants.SnapshotHistorySize);
        }

        /// <summary>
        /// 获取动态 Buffer 历史槽位指针
        /// </summary>
        /// <param name="dynamicDataBuffer">动态数据 Buffer</param>
        /// <param name="historyPosition">Buffer 中的历史位置</param>
        /// <param name="bufferLength">Buffer 长度</param>
        /// <returns>指向动态 Buffer 槽位的指针</returns>
        /// <exception cref="System.IndexOutOfRangeException">位置无效时抛出</exception>
        /// <exception cref="System.InvalidOperationException">Buffer 长度小于 Header 大小时抛出</exception>
        static public byte* GetDynamicDataPtr(byte* dynamicDataBuffer, int historyPosition, int bufferLength)
        {
            var headerSize = GetHeaderSize();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            // 必须按 16 字节对齐
            if (historyPosition < 0 || historyPosition >GhostSystemConstants.SnapshotHistorySize)
                throw new System.IndexOutOfRangeException("invalid history position");
            if(bufferLength < headerSize)
                throw new System.InvalidOperationException($"Snapshot dynamic buffer must always be at least {headerSize} bytes");
#endif
            var slotCapacity = GetDynamicDataCapacity(headerSize, bufferLength);
            return dynamicDataBuffer + headerSize + historyPosition * slotCapacity;
        }
        /// <summary>
        /// 返回每个槽位当前可用的空间，包括掩码和 Buffer 数据
        /// </summary>
        /// <param name="headerSize">Header 大小</param>
        /// <param name="length">总长度</param>
        /// <returns>每个槽位当前可用的空间，包括掩码和 Buffer 数据</returns>
        static public uint GetDynamicDataCapacity(uint headerSize, int length)
        {
            if (length < headerSize)
                return 0;
            return (uint)(length - headerSize) / GhostSystemConstants.SnapshotHistorySize;
        }

        /// <summary>
        /// 返回存储给定动态数据大小所需的历史 Buffer 容量
        /// 并通过输出参数返回每个历史 Buffer 槽位的大小
        /// </summary>
        /// <param name="dynamicDataSize">动态数据大小</param>
        /// <param name="slotSize">槽位大小</param>
        /// <returns>历史 Buffer 容量</returns>
        static public uint CalculateBufferCapacity(uint dynamicDataSize, out uint slotSize)
        {
            var headerSize = GetHeaderSize();
            var newCapacity = headerSize + math.ceilpow2(dynamicDataSize * GhostSystemConstants.SnapshotHistorySize);
            slotSize = (newCapacity - headerSize) / GhostSystemConstants.SnapshotHistorySize;
            return newCapacity;
        }

        /// <summary>
        /// 计算给定元素数量和掩码位数所需的位掩码大小，并按 16 字节对齐
        /// </summary>
        /// <param name="changeMaskBits">变更掩码位数</param>
        /// <param name="numElements">元素数量</param>
        /// <returns>位掩码大小</returns>
        public static int GetDynamicDataChangeMaskSize(int changeMaskBits, int numElements)
        {
            return GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.ChangeMaskArraySizeInUInts(numElements * changeMaskBits)*4);
        }
    }
}
