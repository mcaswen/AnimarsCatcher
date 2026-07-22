using System;
using Unity.Burst;

namespace Unity.NetCode
{
    /// <summary>
    /// 简单的 RAII 风格包装器，用于让 C# 函数委托更方便地兼容 Burst
    /// </summary>
    /// <typeparam name="T">函数委托类型</typeparam>
    public struct PortableFunctionPointer<T> where T : Delegate
    {
        /// <summary>
        /// 将委托转换为兼容 Burst 的函数指针
        /// </summary>
        /// <param name="executeDelegate">函数委托</param>
        public PortableFunctionPointer(T executeDelegate)
        {
            Ptr = BurstCompiler.CompileFunctionPointer(executeDelegate);
        }

        internal readonly FunctionPointer<T> Ptr;
    }
}
