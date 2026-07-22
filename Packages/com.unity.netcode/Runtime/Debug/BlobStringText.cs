using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Unity.NetCode.LowLevel
{
    /// <summary>
    /// 可嵌入组件的简单 <see cref="BlobString"/> 包装器，
    /// 允许通过 <see cref="IUTF8Bytes"/> 和 <see cref="INativeList{T}"/> 访问 Blob 文本
    /// 文本视为只读，所有会修改或影响字符串的方法都会抛出 <see cref="NotImplementedException"/>
    /// </summary>
    public struct BlobStringText: INativeList<byte>, IUTF8Bytes
    {
        [NativeDisableUnsafePtrRestriction] private IntPtr m_Text;
        private int m_Length;

        /// <summary>
        /// 根据 <see cref="BlobString"/> 引用构造文本
        /// 此包装器会在内部缓存字符串指针，如果原始 Blob 被销毁，该内存内容可能不再指向字符串
        /// </summary>
        /// <param name="blob"><see cref="BlobString"/> 引用</param>
        public BlobStringText(ref BlobString blob)
        {
            unsafe
            {
                m_Text = (IntPtr)UnsafeUtility.As<BlobString, BlobArray<byte>>(ref blob).GetUnsafePtr();
            }
            m_Length = blob.Length;
        }

        /// <inheritdoc cref="IUTF8Bytes.IsEmpty"/>
        public bool IsEmpty => m_Length == 0;

        /// <inheritdoc cref="IUTF8Bytes.GetUnsafePtr"/>
        public unsafe byte* GetUnsafePtr()
        {
            return (byte*)m_Text;
        }

        /// <inheritdoc cref="IUTF8Bytes.TryResize"/>
        /// <remarks>始终抛出 NotImplementedException</remarks>
        /// <exception cref="NotImplementedException">始终抛出 NotImplementedException</exception>
        public bool TryResize(int newLength, NativeArrayOptions clearOptions = NativeArrayOptions.ClearMemory)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc cref="INativeList{T}.Length"/>
        /// <remarks>始终抛出 NotImplementedException</remarks>
        /// <exception cref="NotImplementedException">始终抛出 NotImplementedException</exception>
        public int Length
        {
            get => m_Length;
            set => throw new NotImplementedException();
        }
        /// <inheritdoc cref="INativeList{T}.ElementAt"/>
        /// <remarks>始终抛出 NotImplementedException</remarks>
        /// <exception cref="NotImplementedException">始终抛出 NotImplementedException</exception>
        public ref byte ElementAt(int index)
        {
            throw new NotImplementedException();
        }
        /// <inheritdoc cref="INativeList{T}.Capacity"/>
        /// <remarks>始终抛出 NotImplementedException</remarks>
        /// <exception cref="NotImplementedException">始终抛出 NotImplementedException</exception>
        public int Capacity {
            get => m_Length;
            set => throw new NotImplementedException();
        }
        /// <inheritdoc cref="INativeList{T}.this[int]"/>
        /// <remarks>始终抛出 NotImplementedException</remarks>
        /// <exception cref="NotImplementedException">始终抛出 NotImplementedException</exception>
        public byte this[int index]
        {
            get
            {
                unsafe { return *((byte*)m_Text); }
            }
            set => throw new NotImplementedException();
        }
        /// <inheritdoc cref="INativeList{T}.Clear"/>
        /// <remarks>始终抛出 NotImplementedException</remarks>
        /// <exception cref="NotImplementedException">始终抛出 NotImplementedException</exception>
        public void Clear()
        {
            throw new NotImplementedException();
        }
    }
}
