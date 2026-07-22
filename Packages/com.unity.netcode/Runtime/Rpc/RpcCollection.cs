using System;
using Unity.Entities;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// RpcCollection 是全部可用 RPC 的集合，由 RpcSystem 创建
    /// 它用于注册 RPC 并获取 RPC 发送队列
    /// 大多数情况下无需直接使用，生成的代码会通过它配置 RPC Component
    /// </summary>
    public struct RpcCollection : IComponentData
    {
        internal struct RpcData : IComparable<RpcData>
        {
            public ulong TypeHash;
            public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> Execute;
            public byte IsApprovalType;
#if ENABLE_UNITY_COLLECTIONS_CHECKS || UNITY_DOTS_DEBUG
            public ComponentType RpcType;
#endif
            public int CompareTo(RpcData other)
            {
                if (TypeHash < other.TypeHash)
                    return -1;
                if (TypeHash > other.TypeHash)
                    return 1;
                return 0;
            }

            [GenerateTestsForBurstCompatibility]
            public FixedString512Bytes ToFixedString()
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS || UNITY_DOTS_DEBUG
                return (FixedString512Bytes)$"Rpc[{TypeHash}, {RpcType.ToFixedString()}]";
                #else
                return (FixedString512Bytes)$"Rpc[{TypeHash}, ???]";
                #endif
            }
            /// <inheritdoc cref="ToFixedString"/>
            public override string ToString() => ToFixedString().ToString();
        }
        /// <summary>
        /// <para>
        /// 允许客户端和服务器加载不同的程序集集合
        /// 开发期间构建 Standalone 时，包含 Ghost Component Serializer 或 RPC 的程序集可能被移除，此选项很有用
        /// 这种情况通常发生在开发阶段将 Standalone Player 连接到 Editor 时
        /// 例如测试通常不会包含在 Standalone Build 中，但仍会在 Editor 中编译和注册，导致程序集集合不匹配
        /// </para>
        /// <para>
        /// 设为 false，即默认值时，连接到程序集集合不同的服务器会使 RPC 系统触发 RPC 版本错误
        /// 此模式更严格，并会作为 Handshake 期间的一项校验
        /// </para>
        /// <para>
        /// 设为 true 时，每个 RPC 的 Header 会增加 6 字节
        /// 连接到程序集集合不同的服务器时，RPC 系统不会触发 RPC 版本错误
        /// 但收到无效 RPC 或序列化 Component 时会触发错误
        /// </para>
        /// </summary>
        public bool DynamicAssemblyList
        {
            get { return m_DynamicAssemblyList.Value == 1; }
            set
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (m_IsFinal == 1)
                    throw new InvalidOperationException("DynamicAssemblyList must be set before the RpcSystem.OnUpdate is called!");
#endif
                m_DynamicAssemblyList.Value = value ? (byte)1u : (byte)0u;
            }
        }

        /// <summary>
        /// RPC 的 Common Header 格式为 9 字节
        /// - 消息类型：byte
        /// - LocalTime：int，在接收端也称为 `remoteTime`
        ///
        /// 每个 RPC 随后的 Header 如下
        /// - RpcHash：[short|long]，取决于 DynamicAssemblyList
        /// - 大小：ushort
        /// - Payload：x 字节
        ///
        /// 因此单条消息的大小如下
        /// - 9（Common Header）+ 4 => 13 字节，不使用 DynamicAssemblyList
        /// - 9（Common Header）+ 10 => 19 字节，使用 DynamicAssemblyList
        /// </summary>
        /// <param name="dynamicAssemblyList">项目是否使用 <see cref="DynamicAssemblyList"/></param>
        /// <returns>使用 <see cref="DynamicAssemblyList"/> 时为 15 字节，否则为 9 字节</returns>
        public static int GetRpcHeaderLength(bool dynamicAssemblyList) => k_RpcCommonHeaderLengthBytes + GetInnerRpcMessageHeaderLength(dynamicAssemblyList);

        /// <inheritdoc cref="GetRpcHeaderLength"/>>
        internal const int k_RpcCommonHeaderLengthBytes = 5;

        /// <summary>
        /// 使用 <see cref="DynamicAssemblyList"/> 时为 10 字节，否则为 4 字节
        /// </summary>
        /// <param name="dynamicAssemblyList">项目是否使用 <see cref="DynamicAssemblyList"/></param>
        /// <returns>使用 <see cref="DynamicAssemblyList"/> 时为 10 字节，否则为 4 字节</returns>
        internal static int GetInnerRpcMessageHeaderLength(bool dynamicAssemblyList) => dynamicAssemblyList ? 10 : 4;

        /// <summary>
        /// 注册可通过网络发送的新 RPC 类型，必须在建立任何连接前调用
        /// </summary>
        /// <typeparam name="TActionSerializer">IRpcCommandSerializer 类型的结构体</typeparam>
        /// <typeparam name="TActionRequest">IComponent 类型的结构体</typeparam>
        public void RegisterRpc<TActionSerializer, TActionRequest>()
            where TActionRequest : struct, IComponentData
            where TActionSerializer : struct, IRpcCommandSerializer<TActionRequest>
        {
            RegisterRpc(ComponentType.ReadWrite<TActionRequest>(), default(TActionSerializer).CompileExecute());
        }

        /// <summary>
        /// 注册可通过网络发送的新 RPC 类型，必须在建立任何连接前调用
        /// </summary>
        /// <typeparam name="TActionRequestAndSerializer">同时实现 IRpcCommandSerializer 的 IComponentData 结构体</typeparam>
        public void RegisterRpc<TActionRequestAndSerializer>()
            where TActionRequestAndSerializer : struct, IComponentData, IRpcCommandSerializer<TActionRequestAndSerializer>
        {
            RegisterRpc(ComponentType.ReadWrite<TActionRequestAndSerializer>(), default(TActionRequestAndSerializer).CompileExecute());
        }

        /// <summary>
        /// 注册可通过网络发送的新 RPC 类型，必须在建立任何连接前调用
        /// </summary>
        /// <param name="type">要注册的类型</param>
        /// <param name="exec">执行 RPC 的回调</param>
        public void RegisterRpc(ComponentType type, PortableFunctionPointer<RpcExecutor.ExecuteDelegate> exec)
        {
            if (m_IsFinal == 1)
                throw new InvalidOperationException("Cannot register new RPCs after the RpcSystem has started running");

            if (!exec.Ptr.IsCreated)
            {
                throw new InvalidOperationException($"Cannot register RPC for type {type.GetManagedType()}: Ptr property is not created (null)" +
                                                    "Check CompileExecute() and verify you are initializing the PortableFunctionPointer with a valid static function delegate, decorated with [BurstCompile(DisableDirectCall = true)] attribute");
            }

            var hash = TypeManager.GetTypeInfo(type.TypeIndex).StableTypeHash;
            if (hash == 0)
                throw new InvalidOperationException(String.Format("Unexpected 0 hash for type {0}", type.GetManagedType()));

            byte isApprovalType = 0;
            if (IsApprovalRpcType(type))
                isApprovalType = 1;

            if (m_RpcTypeHashToIndex.TryGetValue(hash, out var index))
            {
                var rpcData = m_RpcData[index];
                if (rpcData.TypeHash != 0)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (rpcData.RpcType == type)
                        throw new InvalidOperationException($"Registering RPC {type.ToFixedString()} multiple times is not allowed! Existing: {rpcData.RpcType.ToFixedString()}!");
                    throw new InvalidOperationException($"StableTypeHash collision between types {type.ToFixedString()} and {rpcData.RpcType.ToFixedString()} while registering RPC!");
#else
                    throw new InvalidOperationException($"Hash collision or multiple registrations for {type.ToFixedString()} while registering RPC! Existing: {rpcData.TypeHash}!");
#endif
                }

                rpcData.IsApprovalType = isApprovalType;
                rpcData.TypeHash = hash;
                rpcData.Execute = exec;
                m_RpcData[index] = rpcData;
            }
            else
            {
                m_RpcTypeHashToIndex.Add(hash, m_RpcData.Length);
                m_RpcData.Add(new RpcData
                {
                    TypeHash = hash,
                    Execute = exec,
                    IsApprovalType = isApprovalType,
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    RpcType = type
#endif
                });
            }
        }

        internal static bool IsApprovalRpcType(ComponentType type)
        {
            // TODO：改为通过代码生成推断，避免运行时反射
            return typeof(IApprovalRpcCommand).IsAssignableFrom(type.GetManagedType());
        }

        /// <summary>
        /// 获取可用于发送 RPC 的 RpcQueue
        /// </summary>
        /// <typeparam name="TActionRequestAndSerializer">实现 <see cref="IRpcCommandSerializer{TActionRequestAndSerializer}"/>
        /// 的 <see cref="TActionRequestAndSerializer"/> 类型结构体</typeparam>
        /// <returns>用于发送 RPC 的 <see cref="RpcQueue{TActionRequestAndSerializer,TActionRequestAndSerializer}"/></returns>
        public RpcQueue<TActionRequestAndSerializer, TActionRequestAndSerializer> GetRpcQueue<TActionRequestAndSerializer>()
            where TActionRequestAndSerializer : struct, IComponentData, IRpcCommandSerializer<TActionRequestAndSerializer>
        {
            return GetRpcQueue<TActionRequestAndSerializer, TActionRequestAndSerializer>();
        }

        /// <summary>
        /// 获取可用于发送 RPC 的 RpcQueue
        /// </summary>
        /// <typeparam name="TActionSerializer"><see cref="IRpcCommandSerializer{TActionRequest}"/> 类型的结构体</typeparam>
        /// <typeparam name="TActionRequest"><see cref="IComponentData"/> 类型的结构体</typeparam>
        /// <returns>用于发送 RPC 的 <see cref="RpcQueue{TActionSerializer,TActionRequest}"/></returns>
        public RpcQueue<TActionSerializer, TActionRequest> GetRpcQueue<TActionSerializer, TActionRequest>()
            where TActionRequest : struct, IComponentData
            where TActionSerializer : struct, IRpcCommandSerializer<TActionRequest>
        {
            var hash = TypeManager.GetTypeInfo(TypeManager.GetTypeIndex<TActionRequest>()).StableTypeHash;
            if (hash == 0)
                throw new InvalidOperationException(String.Format("Unexpected 0 hash for type {0}", typeof(TActionRequest)));
            int index;
            if (!m_RpcTypeHashToIndex.TryGetValue(hash, out index))
            {
                if (m_IsFinal == 1)
                    throw new InvalidOperationException("Cannot register new RPCs after the RpcSystem has started running");
                index = m_RpcData.Length;
                m_RpcTypeHashToIndex.Add(hash, index);
                m_RpcData.Add(new RpcData
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    RpcType = ComponentType.ReadWrite<TActionRequest>()
#endif
                });
            }
            return new RpcQueue<TActionSerializer, TActionRequest>
            {
                rpcType = hash,
                rpcTypeHashToIndex = m_RpcTypeHashToIndex,
                dynamicAssemblyList = m_DynamicAssemblyList
            };
        }
        /// <summary>
        /// 发送版本时计算所有类型 Hash 的内部方法
        /// 此方法会改变内部状态，因此调用方必须拥有 Singleton 的写权限
        /// </summary>
        internal ulong CalculateVersionHash()
        {
            Debug.Assert(m_IsFinal == 0);
            if (m_RpcData.Length >= ushort.MaxValue)
                throw new InvalidOperationException(String.Format("RpcSystem does not support more than {0} RPCs", ushort.MaxValue));
            for (int i = 0; i < m_RpcData.Length; ++i)
            {
                if (m_RpcData[i].TypeHash == 0)
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    throw new InvalidOperationException(String.Format("Missing RPC registration for {0} which is used to send data", m_RpcData[i].RpcType.GetManagedType()));
#else
                    throw new InvalidOperationException("Missing RPC registration for RPC which is used to send");
#endif
            }
            m_RpcData.Sort();
            m_RpcTypeHashToIndex.Clear();
            for (int i = 0; i < m_RpcData.Length; ++i)
            {
                m_RpcTypeHashToIndex.Add(m_RpcData[i].TypeHash, i);

#if ENABLE_UNITY_RPC_REGISTRATION_LOGGING
#if UNITY_DOTS_DEBUG
                UnityEngine.Debug.Log(String.Format("NetCode RPC Method hash 0x{0:X} index {1} type {2}", m_RpcData[i].TypeHash, i, m_RpcData[i].RpcType));
#else
                UnityEngine.Debug.Log(String.Format("NetCode RPC Method hash {0} index {1}", m_RpcData[i].TypeHash, i));
#endif
#endif
            }

            ulong hash = m_RpcData[0].TypeHash;
            for (int i = 0; i < m_RpcData.Length; ++i)
                hash = TypeHash.CombineFNV1A64(hash, m_RpcData[i].TypeHash);
            m_IsFinal = 1;
            return hash;
        }

        internal NativeList<RpcData> Rpcs => m_RpcData;

        internal NativeList<RpcData> m_RpcData;
        internal NativeParallelHashMap<ulong, int> m_RpcTypeHashToIndex;
        internal NativeReference<byte> m_DynamicAssemblyList;

        internal byte m_IsFinal;
    }
}
