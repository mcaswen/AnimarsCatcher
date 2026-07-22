using System;
using System.Diagnostics;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Properties;

namespace Unity.NetCode
{
    /// <summary>
    /// 表示网络 Tick 的简单结构体
    /// 内部使用 uint，并通过特殊逻辑处理无效 Tick 和数值回绕
    /// </summary>
    [Serializable]
    public struct NetworkTick : IEquatable<NetworkTick>
    {
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckValid()
        {
            if(Hint.Unlikely(!IsValid))
                throw new InvalidOperationException("Cannot perform calculations with invalid ticks");
        }
        /// <summary>
        /// 表示无效 Tick 的值，与 default 相同，但能在代码中提供更明确的语义
        /// </summary>
        public static NetworkTick Invalid => default;
        /// <summary>
        /// 比较两个 Tick，也适用于无效 Tick
        /// </summary>
        /// <param name="a">左侧 Tick</param>
        /// <param name="b">右侧 Tick</param>
        /// <returns>两个 Tick 值是否相等</returns>
        public static bool operator ==(in NetworkTick a, in NetworkTick b)
        {
            return a.m_Value == b.m_Value;
        }
        /// <summary>
        /// 比较两个 Tick，也适用于无效 Tick
        /// </summary>
        /// <param name="a">左侧 Tick</param>
        /// <param name="b">右侧 Tick</param>
        /// <returns>两个 Tick 值是否不同</returns>
        public static bool operator !=(in NetworkTick a, in NetworkTick b)
        {
            return a.m_Value != b.m_Value;
        }
        /// <summary>
        /// 比较两个 Tick，也适用于无效 Tick
        /// </summary>
        /// <inheritdoc cref="object.Equals(object)"/>
        public override bool Equals(object obj) => obj is NetworkTick && Equals((NetworkTick) obj);
        /// <summary>
        /// 比较两个 Tick，也适用于无效 Tick
        /// </summary>
        /// <param name="compare">要比较的网络 Tick</param>
        /// <returns><paramref name="compare"/> 是否具有相同 Tick 值</returns>
        public bool Equals(NetworkTick compare)
        {
            return m_Value == compare.m_Value;
        }
        /// <summary>
        /// 获取 Tick 的 Hash
        /// </summary>
        /// <returns>内部 Tick 值</returns>
        public override int GetHashCode()
        {
            return (int)m_Value;
        }

        /// <summary>
        /// 构造函数，起始 Tick 可以为 0
        /// 默认构造函数会生成无效 Tick，因此应改用此构造函数
        /// </summary>
        /// <param name="start">用于初始化 NetworkTick 的 Tick 索引</param>
        public NetworkTick(uint start)
        {
            m_Value = (start<<1) | 1u;
        }
        /// <summary>
        /// 检查 Tick 是否有效，并非所有操作都支持无效 Tick
        /// </summary>
        public bool IsValid => (m_Value&1)!=0;
        /// <summary>
        /// 在 Tick 有效的前提下获取其索引
        /// Tick 会发生回绕，因此使用时需要谨慎
        /// </summary>
        public uint TickIndexForValidTick
        {
            get
            {
                CheckValid();
                return m_Value>>1;
            }
        }
        /// <summary>
        /// Tick 的序列化数据，包含有效性和 Tick 索引
        /// </summary>
        public uint SerializedData
        {
            get
            {
                return m_Value;
            }
            set
            {
                m_Value = value;
            }
        }
        /// <summary>
        /// 为 Tick 加上增量，要求 Tick 有效
        /// </summary>
        /// <param name="delta">要加到 Tick 上的值</param>
        public void Add(uint delta)
        {
            CheckValid();
            m_Value += delta<<1;
        }
        /// <summary>
        /// 从 Tick 中减去增量，要求 Tick 有效
        /// </summary>
        /// <param name="delta">要从 Tick 中减去的值</param>
        public void Subtract(uint delta)
        {
            CheckValid();
            m_Value -= delta<<1;
        }
        /// <summary>
        /// 将 Tick 加一，要求 Tick 有效
        /// </summary>
        public void Increment()
        {
            CheckValid();
            m_Value += 2;
        }
        /// <summary>
        /// 将 Tick 减一，要求 Tick 有效
        /// </summary>
        public void Decrement()
        {
            CheckValid();
            m_Value -= 2;
        }
        /// <summary>
        /// 计算从较旧 Tick 至今经过的 Tick 数量，要求两个 Tick 都有效
        /// 如果传入的 Tick 更新，则返回负值
        /// </summary>
        /// <param name="older">用于计算经过 Tick 数量的起始 Tick</param>
        /// <returns>从 <paramref name="older"/> 开始经过的 Tick 数量</returns>
        public int TicksSince(NetworkTick older)
        {
            CheckValid();
            older.CheckValid();
            // 先转换为 int，确保负值在移位后仍为负值
            int delta = (int)(m_Value-older.m_Value);
            return delta>>1;
        }
        /// <summary>
        /// 检查此 Tick 是否比另一个 Tick 更新，要求两个 Tick 都有效
        /// </summary>
        /// <remarks>
        /// Tick 会发生回绕，因此任一 Tick 保存时间过长时结果可能不正确
        /// 以 60Hz 为例，数天后就可能出现该情况
        /// </remarks>
        /// <param name="old">要比较的 Tick</param>
        /// <returns>此 Tick 是否比另一个 Tick 更新</returns>
        public bool IsNewerThan(NetworkTick old)
        {
            CheckValid();
            old.CheckValid();
            // 反转检查结果，避免将相同 Tick 判定为更新
            return !(old.m_Value - m_Value < (1u << 31));
        }
        /// <summary>
        /// 将 Tick 转换为 FixedString，也能处理无效 Tick
        /// </summary>
        /// <returns>以 FixedString 表示的 Tick 索引，无效 Tick 返回 "Invalid"</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString32Bytes ToFixedString()
        {
            if (IsValid)
            {
                FixedString32Bytes val = default;
                val.Append(m_Value>>1);
                return val;
            }
            return "Invalid";
        }

        /// <summary>
        /// 调用 <see cref="ToFixedString"/>
        /// </summary>
        /// <returns>以字符串表示的 Tick 索引，无效 Tick 返回 "Invalid"</returns>
        public override string ToString() => ToFixedString().ToString();

        /// <summary>
        /// 用于在 Entity Inspector 中无异常显示 Tick 的辅助属性
        /// </summary>
        [CreateProperty]
        public FixedString32Bytes TickValue => ToFixedString();

        private uint m_Value;
    }
}
