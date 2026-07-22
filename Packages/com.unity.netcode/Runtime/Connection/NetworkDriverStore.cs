using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    /// <summary>
    /// NetworkDriver 使用的 Transport 类别或类型
    /// </summary>
    public enum TransportType : int
    {
        /// <summary>
        /// 未配置或不受支持的 Transport 接口
        /// 除非 Driver 创建失败，否则已注册 Driver 实例的 Transport 类型始终有效，不会是此值
        /// </summary>
        Invalid = 0,
        /// <summary>
        /// 零延迟且保证送达的进程间通信通道
        /// </summary>
        IPC,
        /// <summary>
        /// 基于 Socket 的通信通道，WebSocket、UDP、TCP 及类似通道都属于此类别
        /// </summary>
        Socket,
    }

    /// <summary>
    /// 保存并管理 NetworkDriver 数组，容量固定为 <see cref="Capacity"/>
    /// Driver 注册应从调用 BeginDriverRegistration() 开始，并以 EndDriverRegistration() 结束
    /// 此 Store 还提供若干访问器和工具方法
    /// </summary>
    public struct NetworkDriverStore
    {
        /// <summary>
        /// 包含 <see cref="NetworkDriver"/> 及相关 Pipeline 的结构体
        /// </summary>
        public struct NetworkDriverInstance
        {
            /// <summary>
            /// <see cref="NetworkDriver"/> 实例，尚未初始化时可能无效
            /// </summary>
            public NetworkDriver driver;
            /// <summary>
            /// 用于发送可靠消息的 Pipeline
            /// </summary>
            public NetworkPipeline reliablePipeline;
            /// <summary>
            /// 用于发送不可靠消息和 Snapshot 的 Pipeline
            /// </summary>
            public NetworkPipeline unreliablePipeline;
            /// <summary>
            /// 用于发送需要分片的大型不可靠消息的 Pipeline
            /// </summary>
            public NetworkPipeline unreliableFragmentedPipeline;
            /// <summary>
            /// Driver Pipeline 使用 <see cref="SimulatorPipelineStage"/> 时设置的标志
            /// </summary>
            public bool simulatorEnabled
            {
                get => driver.IsCreated && driver.CurrentSettings.TryGet<SimulatorUtility.Parameters>(out _) || driver.CurrentSettings.TryGet<NetworkSimulatorParameter>(out _);
                [Obsolete("This set has no effect on whether or not the simulator is actually enabled, and therefore should not be used.", false)]
                // ReSharper disable once ValueParameterNotUsed
                set { }
            }

            internal void StopListening()
            {
                #pragma warning disable 0618
                driver.StopListening();
                #pragma warning restore 0618
            }
        }

        /// <summary>
        /// 包含 <see cref="NetworkDriver"/> 的 <see cref="NetworkDriver.Concurrent"/> 版本及相关 Pipeline 的结构体
        /// </summary>
        public struct Concurrent
        {
            /// <summary>
            /// NetworkDriver 的 <see cref="NetworkDriver.Concurrent"/> 版本
            /// </summary>
            public NetworkDriver.Concurrent driver;
            /// <summary>
            /// 用于发送可靠消息的 Pipeline
            /// </summary>
            public NetworkPipeline reliablePipeline;
            /// <summary>
            /// 用于发送不可靠消息和 Snapshot 的 Pipeline
            /// </summary>
            public NetworkPipeline unreliablePipeline;
            /// <summary>
            /// 用于发送需要分片的大型不可靠消息的 Pipeline
            /// </summary>
            public NetworkPipeline unreliableFragmentedPipeline;
        }

        internal struct NetworkDriverData
        {
            public NetworkDriverInstance instance;
            public TransportType transportType;

            public void Dispose()
            {
                if (instance.driver.IsCreated)
                    instance.driver.Dispose();
            }

            public bool IsCreated => instance.driver.IsCreated;
        }

        internal NetworkDriverData m_Driver0;
        internal NetworkDriverData m_Driver1;
        internal NetworkDriverData m_Driver2;
        private int m_numDrivers;
        private int m_Finalized;

        /// <summary>
        /// Driver 容器的固定容量
        /// </summary>
        public const int Capacity = 3;
        /// <summary>
        /// 分配给 Driver 的首个唯一标识符
        /// </summary>
        public const int FirstDriverId = 1;
        /// <summary>
        /// 已注册 Driver 数量，必须始终小于 Driver 总 <see cref="Capacity"/>
        /// </summary>
        public readonly int DriversCount => m_numDrivers;
        /// <summary>
        /// Store 中首个 Driver ID
        /// 可用于在 for 循环中遍历全部已注册 Driver
        /// </summary>
        /// <example><code>
        /// for(int i= driverStore.FirstDriver; i &lt; driverStore.LastDriver; ++i)
        /// {
        ///      ref var instance = ref driverStore.GetDriverInstance(i);
        ///      ....
        /// }
        /// </code></example>
        public readonly int FirstDriver => FirstDriverId;
        /// <summary>
        /// Store 中末尾边界 Driver ID
        /// 可用于在 for 循环中遍历全部已注册 Driver
        /// </summary>
        /// <example><code>
        /// for(int i= driverStore.FirstDriver; i &lt; driverStore.LastDriver; ++i)
        /// {
        ///      ref var instance = ref driverStore.GetDriverInstance(i).
        ///      ....
        /// }
        /// </code></example>
        public readonly int LastDriver => FirstDriverId + m_numDrivers;
        /// <summary>
        /// Driver Store 中包含具有 Simulator Pipeline 的 Driver 时返回 true
        /// </summary>
        public readonly bool IsAnyUsingSimulator
        {
            get
            {
                for (var i = FirstDriver; i < LastDriver; ++i)
                {
                    if (GetDriverInstanceRO(i).simulatorEnabled)
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// 至少有一个 Driver 正在监听入站连接时返回 true
        /// </summary>
        public bool HasListeningInterfaces
        {
            get
            {
                for (var i = FirstDriver; i < LastDriver; ++i)
                {
                    ref readonly var driverInstance = ref GetDriverInstanceRO(i);
                    if (driverInstance.driver.IsCreated && driverInstance.driver.Listening)
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Store 是否至少注册了一个 Driver
        /// </summary>
        public bool IsCreated => m_numDrivers > 0 && m_Driver0.IsCreated;

        /// <summary>
        /// 向 Store 添加新 Driver
        /// 所有 Driver 槽位均被占用，或 Driver 尚未创建或无效时抛出异常
        /// </summary>
        /// <returns>分配的 Driver ID</returns>
        /// <param name="driverType">Driver 类型</param>
        /// <param name="driverInstance">Driver 实例</param>
        /// <exception cref="InvalidOperationException">无法注册或 NetworkDriverStore 已 Finalize 时抛出</exception>
        public int RegisterDriver(TransportType driverType, in NetworkDriverInstance driverInstance)
        {
            if (driverInstance.driver.IsCreated == false)
                throw new InvalidOperationException("Cannot register non valid driver (IsCreated == false)");
            if (m_numDrivers == Capacity)
                throw new InvalidOperationException("Cannot register more driver. All slot are already used");
            if(m_Finalized != 0)
                throw new InvalidOperationException("It is invalid to register a NetworkDriver instance to an already finalized NetworkDriverStore.\nIn order to register a new driver, you need to create a new NetworkDriverStore or invoke the RegisterNetworkDriver before the store instance is assigned to NetworkStreamDriver.");
            int nextDriverId = FirstDriverId + m_numDrivers;
            ++m_numDrivers;
            ref var driverRef = ref GetDriverDataRW(nextDriverId);
            if (driverRef.IsCreated)
                driverRef.Dispose();
            driverRef.transportType = driverType;
            driverRef.instance = driverInstance;
            return nextDriverId;
        }


        /// <summary>
        /// 使用 NullNetworkInterface 初始化所有缺失的 Driver 实例，以完成注册阶段
        /// 这个最终步骤用于确保 Job Safety System 能跟踪全部 Safety Handle
        /// </summary>
        internal void FinalizeDriverStore()
        {
            if (m_Finalized != 0)
                throw new InvalidOperationException("FinalizeDriverStore is called on already finalized NetworkDriverStore instance.");
            // 此条件编译用于避免在非必要时分配 Driver 内部数据
            // 只有启用 Safety Handle 时才需要分配全部 Driver
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!m_Driver0.IsCreated)
                m_Driver0.instance.driver = NetworkDriver.Create(new NullNetworkInterface());
            if (!m_Driver1.IsCreated)
                m_Driver1.instance.driver = NetworkDriver.Create(new NullNetworkInterface());
            if (!m_Driver2.IsCreated)
                m_Driver2.instance.driver = NetworkDriver.Create(new NullNetworkInterface());
#endif
        }

        /// <summary>
        /// 返回可在并行 Job 中使用的 Store 并发版本
        /// </summary>
        internal ConcurrentDriverStore ToConcurrent()
        {
            var store = new ConcurrentDriverStore();
            // 此判断不可省略，因为未定义 ENABLE_UNITY_COLLECTIONS_CHECKS 时不会创建全部 Driver 实例
            if (m_Driver0.IsCreated)
                store.m_Concurrent0 = new Concurrent
                {
                    driver = m_Driver0.instance.driver.ToConcurrent(),
                    reliablePipeline = m_Driver0.instance.reliablePipeline,
                    unreliablePipeline = m_Driver0.instance.unreliablePipeline,
                    unreliableFragmentedPipeline = m_Driver0.instance.unreliableFragmentedPipeline,
                };
            if (m_Driver1.IsCreated)
                store.m_Concurrent1 = new Concurrent
                {
                    driver = m_Driver1.instance.driver.ToConcurrent(),
                    reliablePipeline = m_Driver1.instance.reliablePipeline,
                    unreliablePipeline = m_Driver1.instance.unreliablePipeline,
                    unreliableFragmentedPipeline = m_Driver1.instance.unreliableFragmentedPipeline,
                };
            if (m_Driver2.IsCreated)
                store.m_Concurrent2 = new Concurrent
                {
                    driver = m_Driver2.instance.driver.ToConcurrent(),
                    reliablePipeline = m_Driver2.instance.reliablePipeline,
                    unreliablePipeline = m_Driver2.instance.unreliablePipeline,
                    unreliableFragmentedPipeline = m_Driver2.instance.unreliableFragmentedPipeline,
                };
            return store;
        }

        /// <summary>
        /// 释放全部已注册 Driver 实例及其分配的资源
        /// </summary>
        public void Dispose()
        {
            m_Driver0.Dispose();
            m_Driver1.Dispose();
            m_Driver2.Dispose();
        }

        /// <summary>
        /// 以只读引用返回 <see cref="NetworkDriverData"/> 实例
        /// </summary>
        /// <param name="driverId">目标 Driver 的索引，参见 <see cref="FirstDriver"/> 和 <see cref="LastDriver"/></param>
        /// <returns><see cref="NetworkDriverData"/> 实例的只读引用</returns>
        /// <exception cref="InvalidOperationException">driverId 超出范围时抛出</exception>
        internal readonly unsafe ref readonly NetworkDriverData GetDriverDataRO(int driverId)
        {
            CheckValid(driverId);
            fixed (NetworkDriverStore* store = &this)
            {
                switch (driverId)
                {
                    case 1: return ref store->m_Driver0;
                    case 2: return ref store->m_Driver1;
                    case 3: return ref store->m_Driver2;
                    default:
                        throw new InvalidOperationException($"Cannot find NetworkDriver with id {driverId}");
                }
            }
        }
        /// <summary>
        /// 以引用返回 <see cref="NetworkDriverData"/> 实例
        /// </summary>
        /// <inheritdoc cref="GetDriverDataRO"/>
        internal readonly unsafe ref NetworkDriverData GetDriverDataRW(int driverId)
        {
            CheckValid(driverId);
            fixed (NetworkDriverStore* store = &this)
            {
                switch (driverId)
                {
                    case 1: return ref store->m_Driver0;
                    case 2: return ref store->m_Driver1;
                    case 3: return ref store->m_Driver2;
                    default:
                        throw new InvalidOperationException($"Cannot find NetworkDriver with id {driverId}");
                }
            }
        }

        /// <summary>
        /// 返回指定 <see cref="driverId"/> 对应的 <see cref="NetworkDriverInstance"/> 实例
        /// </summary>
        /// <remarks>
        /// 此方法返回 Driver 实例的副本而不是引用
        /// 由于 Driver 可以简单复制，这适合绝大多数使用场景
        /// 但调用 ScheduleUpdate 等会更新不适合复制的 Driver 内部数据的方法时，行为可能不符合预期
        /// </remarks>
        /// <inheritdoc cref="GetDriverDataRO"/>
        [Obsolete("Prefer GetDriverInstanceRW or GetDriverInstanceRO to avoid copying.", false)]
        public readonly ref NetworkDriverInstance GetDriverInstance(int driverId) => ref GetDriverDataRW(driverId).instance;

        /// <summary>
        /// 返回指定 <see cref="driverId"/> 对应的 <see cref="NetworkDriver"/>
        /// </summary>
        /// <inheritdoc cref="GetDriverDataRO"/>
        [Obsolete("Prefer GetDriverRW or GetDriverRO to avoid copying.", false)]
        public readonly NetworkDriver GetNetworkDriver(int driverId) => GetDriverDataRO(driverId).instance.driver;

        /// <summary>
        /// 返回指定 <see cref="driverId"/> 对应 <see cref="NetworkDriverStore.NetworkDriverInstance"/> 实例的引用
        ///  </summary>
        /// <inheritdoc cref="GetDriverDataRO"/>
        public readonly ref NetworkDriverStore.NetworkDriverInstance GetDriverInstanceRW(int driverId) => ref GetDriverDataRW(driverId).instance;

        /// <summary>
        /// 返回指定 <see cref="driverId"/> 对应 <see cref="NetworkDriverStore.NetworkDriverInstance"/> 实例的只读引用
        ///  </summary>
        /// <inheritdoc cref="GetDriverDataRO"/>
        public readonly ref readonly NetworkDriverStore.NetworkDriverInstance GetDriverInstanceRO(int driverId) => ref GetDriverDataRO(driverId).instance;

        /// <summary>
        /// 获取指定 <see cref="driverId"/> 对应 <see cref="NetworkDriver"/> 的读写引用
        /// </summary>
        /// <inheritdoc cref="GetDriverDataRO"/>
        public readonly ref NetworkDriver GetDriverRW(int driverId) => ref GetDriverInstanceRW(driverId).driver;

        /// <summary>
        /// 获取指定 <see cref="driverId"/> 对应 <see cref="NetworkDriver"/> 的只读引用
        /// </summary>
        /// <inheritdoc cref="GetDriverDataRO"/>
        public readonly ref readonly NetworkDriver GetDriverRO(int driverId) => ref GetDriverInstanceRO(driverId).driver;

        /// <summary>
        /// 返回已注册 Driver 使用的 Transport 类型
        /// </summary>
        /// <inheritdoc cref="GetDriverDataRO"/>
        public readonly TransportType GetDriverType(int driverId) => GetDriverDataRO(driverId).transportType;

        /// <summary>
        /// 返回 <see cref="NetworkStreamConnection"/> 的连接状态
        /// </summary>
        /// <param name="connection">客户端或服务器连接</param>
        /// <returns><see cref="NetworkStreamConnection"/> 的连接状态</returns>
        /// <exception cref="InvalidOperationException">找不到与连接关联的 Driver 时抛出</exception>
        public readonly NetworkConnection.State GetConnectionState(NetworkStreamConnection connection) => GetDriverRW(connection.DriverId).GetConnectionState(connection.Value);

        /// <summary>
        /// 可通过 <see cref="ForEachDriver"/> 方法访问 Store 中已注册 Driver 的全部函数签名
        /// </summary>
        /// <param name="driver"><see cref="NetworkDriverInstance"/> 的引用</param>
        /// <param name="driverId">Driver ID，必须始终大于或等于 <see cref="NetworkDriverStore.FirstDriverId"/></param>
        public delegate void DriverVisitor(ref NetworkDriverInstance driver, int driverId);

        /// <summary>
        /// 对全部已注册 Driver 调用委托
        /// </summary>
        /// <param name="visitor">使用 Driver 实例和 ID 调用的 Visitor</param>
        [Obsolete("The ForEachDriver has been deprecated. Please always iterate over the driver using a for loop, using the FirstDriver and LastDriver ids instead.")]
        public void ForEachDriver(DriverVisitor visitor)
        {
            if (m_numDrivers == 0)
                return;
            visitor(ref m_Driver0.instance, FirstDriverId);
            if (m_numDrivers > 1)
                visitor(ref m_Driver1.instance, FirstDriverId + 1);
            if (m_numDrivers > 2)
                visitor(ref m_Driver2.instance, FirstDriverId + 2);
        }

        /// <summary>
        /// 断开 <see cref="NetworkStreamConnection" /> 的工具方法
        /// </summary>
        /// <inheritdoc cref="GetDriverRW"/>
        public void Disconnect(NetworkStreamConnection connection) => GetDriverRW(connection.DriverId).Disconnect(connection.Value);

        internal JobHandle ScheduleUpdateAllDrivers(JobHandle dependency)
        {
            if (m_numDrivers == 0)
                return dependency;
            JobHandle driver0 = m_Driver0.instance.driver.ScheduleUpdate(dependency);
            JobHandle driver1 = default, driver2 = default;
            if (m_numDrivers > 1)
                driver1 = m_Driver1.instance.driver.ScheduleUpdate(dependency);
            if (m_numDrivers > 2)
                driver2 = m_Driver2.instance.driver.ScheduleUpdate(dependency);
            return JobHandle.CombineDependencies(driver0, driver1, driver2);
        }

        /// <summary>
        /// 对 Store 中全部已注册 Driver 调用 <see cref="NetworkDriver.ScheduleFlushSend"/>
        /// </summary>
        /// <param name="dependency">全部 Flush Job 依赖的 JobHandle</param>
        /// <returns>所有已调度 Job 的组合句柄</returns>
        public JobHandle ScheduleFlushSendAllDrivers(JobHandle dependency)
        {
            if (m_numDrivers == 0)
                return dependency;
            JobHandle driver0 = m_Driver0.instance.driver.ScheduleFlushSend(dependency);
            JobHandle driver1 = default, driver2 = default;
            if (m_numDrivers > 1)
                driver1 = m_Driver1.instance.driver.ScheduleFlushSend(dependency);
            if (m_numDrivers > 2)
                driver2 = m_Driver2.instance.driver.ScheduleFlushSend(dependency);
            return JobHandle.CombineDependencies(driver0, driver1, driver2);
        }

        private readonly void CheckValid(int driverId)
        {
            var isValidDriverId = driverId >= FirstDriverId && driverId < LastDriver;
            if (!isValidDriverId)
                throw new InvalidOperationException($"DriverId:{driverId} out of range: {FirstDriverId} -> {LastDriver}!");
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        /// <summary>
        /// 仅供内部使用且不执行任何操作的网络接口
        /// <see cref="NetworkDriverStore"/> 中所有未注册的 NetworkDriver 槽位都会使用此接口初始化
        /// </summary>
        internal struct NullNetworkInterface : INetworkInterface
        {
            public NetworkEndpoint LocalEndpoint => throw new NotImplementedException();

            public int Bind(NetworkEndpoint endpoint) => throw new NotImplementedException();

            public void Dispose() { }

            public int Initialize(ref NetworkSettings settings, ref int packetPadding) => 0;

            public int Listen() => throw new NotImplementedException();

            public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dep) => throw new NotImplementedException();

            public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dep) => throw new NotImplementedException();
        }
#endif
    }

    /// <summary>
    /// DriverStore 的并发版本，包含 Driver 及相关 Pipeline 的并发副本
    /// </summary>
    public struct ConcurrentDriverStore
    {
        internal NetworkDriverStore.Concurrent m_Concurrent0;
        internal NetworkDriverStore.Concurrent m_Concurrent1;
        internal NetworkDriverStore.Concurrent m_Concurrent2;

        /// <summary>
        /// 获取指定 Driver ID 对应的并发 Driver
        /// </summary>
        /// <param name="driverId">Driver ID，必须始终大于或等于 <see cref="NetworkDriverStore.FirstDriverId"/></param>
        /// <returns>NetworkDriverStore 的并发版本</returns>
        /// <exception cref="InvalidOperationException">driverId 超出范围时抛出</exception>
        public NetworkDriverStore.Concurrent GetConcurrentDriver(int driverId)
        {
            var concurrent = driverId switch
            {
                1 => m_Concurrent0,
                2 => m_Concurrent1,
                3 => m_Concurrent2,
                _ => throw new InvalidOperationException($"Concurrent driverId:{driverId} out of range!"),
            };
            if (!concurrent.driver.m_ConnectionList.IsCreated)
                throw new InvalidOperationException($"Concurrent driverId:{driverId} invalid!");
            return concurrent;
        }
    }
}
