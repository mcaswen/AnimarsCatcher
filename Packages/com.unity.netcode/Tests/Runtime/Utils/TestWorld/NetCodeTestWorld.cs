#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Core;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Profiling;
using Unity.Transforms;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;
#if UNITY_EDITOR
using Unity.NetCode.Editor;
#endif
using UnityEngine;
using Object = UnityEngine.Object;
#if USING_UNITY_LOGGING
using Unity.Logging;
using Unity.Logging.Sinks;
#endif

namespace Unity.NetCode.Tests
{
    internal struct NetCodeTestPrefabCollection : IComponentData
    {}
    internal struct NetCodeTestPrefab : IBufferElementData
    {
        public Entity Value;
    }

    // 用于兼容 2022 版本中不存在 Awaitable 的情况，避免在各处重复编写条件编译分支
    internal class NetcodeAwaitable : IEnumerator, INotifyCompletion
    {
#if UNITY_6000_0_OR_NEWER
        internal Awaitable awaitable;
#endif

        public bool MoveNext()
        {
#if UNITY_6000_0_OR_NEWER
            return ((IEnumerator)awaitable).MoveNext();
#else
            return false;
#endif
        }

        public void Reset()
        {
#if UNITY_6000_0_OR_NEWER
            ((IEnumerator)awaitable).Reset();
#endif
        }

#if UNITY_6000_0_OR_NEWER
        public object Current => ((IEnumerator)awaitable).Current;
#else
        public object Current => null;
#endif

        public NetcodeAwaitable GetAwaiter() => this;

        public bool IsCompleted => !MoveNext();

        public void OnCompleted(Action continuation)
        {
#if UNITY_6000_0_OR_NEWER
            if (awaitable is INotifyCompletion awaitableCompletion)
                awaitableCompletion.OnCompleted(continuation);
            else
                continuation();
#endif
        }

        public void GetResult()
        {

        }

#if UNITY_6000_0_OR_NEWER
        public static implicit operator NetcodeAwaitable(Awaitable awaitable)
        {
            return new NetcodeAwaitable { awaitable = awaitable };
        }
#endif
    }

    internal class NetCodeTestWorld : IDisposable, INetworkStreamDriverConstructor
    {
        internal interface ITestWorldStrategy : IDisposable
        {
            void Bootstrap(NetCodeTestWorld testWorld);
            World CreateClientWorld(string name, bool thinClient, World world = null);
            World CreateServerWorld(string name, World world = null);
            World CreateHostWorld(string name, World world = null);
            void DisposeClientWorld(World clientWorld);
            void DisposeServerWorld(World serverWorld);
            void TickNoAwait(float dt);
            Task TickAsync(float dt, NetcodeAwaitable waitInstruction = null);
            void TickClientWorld(float dt);
            void TickServerWorld(float dt);
            void RemoveWorldFromUpdateList(World world);
            World DefaultWorld { get; }
            void DisposeDefaultWorld();
        }

        /// <summary>
        /// 是否转发服务端的全部 NetCode 日志，以便使用 <see cref="LogAssert"/>
        /// <b>警告：必须禁用 "Force Log Settings" 工具，否则测试会失败</b>
        /// </summary>
        /// <remarks>默认为 true，另请参阅 <see cref="DebugPackets"/> 和 <see cref="LogLevel"/></remarks>
        public bool EnableLogsOnServer = true;
        /// <summary>
        /// 是否转发客户端的全部 NetCode 日志，以便使用 <see cref="LogAssert"/>
        /// <b>警告：必须禁用 "Force Log Settings" 工具，否则测试会失败</b>
        /// </summary>
        /// <remarks>默认为 true，另请参阅 <see cref="DebugPackets"/> 和 <see cref="LogLevel"/></remarks>
        public bool EnableLogsOnClients = true;

        /// <summary>
        /// 是否在测试中启用数据包转储，可用于确认序列化没有失败
        /// </summary>
        /// <remarks>数据包转储文件不会自动清理</remarks>
        public bool DebugPackets = false;
        /// <summary>
        /// 测试高详细度日志时可修改此级别
        /// </summary>
        public NetDebug.LogLevelType LogLevel = NetDebug.LogLevelType.Notify;

        static readonly ProfilerMarker k_TickServerInitializationSystem = new ProfilerMarker("TickServerInitializationSystem");
        static readonly ProfilerMarker k_TickClientInitializationSystem = new ProfilerMarker("TickClientInitializationSystem");
        static readonly ProfilerMarker k_TickServerSimulationSystem = new ProfilerMarker("TickServerSimulationSystem");
        static readonly ProfilerMarker k_TickClientSimulationSystem = new ProfilerMarker("TickClientSimulationSystem");
        static readonly ProfilerMarker k_TickClientPresentationSystem = new ProfilerMarker("TickClientPresentationSystem");

        public World DefaultWorld => m_WorldStrategy.DefaultWorld;
        public World ServerWorld
        {
            get { return m_ServerWorld; }
            set { m_ServerWorld = value; }
        }
        public World[] ClientWorlds
        {
            get
            {
                if (m_ClientWorlds == null) return null;
                return m_ClientWorlds.ToArray();
            }
        }

        /// <summary>
        /// 记录 <see cref="Tick"/> 的调用次数，索引从零开始
        /// 调用前为 -1，首帧为 0
        /// </summary>
        public static int TickIndex { get; private set; }

        private World m_DefaultWorld => m_WorldStrategy.DefaultWorld;
        public List<World> m_ClientWorlds;
        public World m_ServerWorld;
        private ushort m_OldBootstrapAutoConnectPort;
        private bool m_DefaultWorldInitialized;
        private double m_ElapsedTime;
        public int MaxFrameTime = 100;
        public int DriverFixedTime = 16;
        public int ConnectTimeout = NetworkParameterConstants.ConnectTimeoutMS;
        public int MaxConnectAttempts = NetworkParameterConstants.MaxConnectAttempts;
        public int DriverSimulatedDelay = 0; // 毫秒
        public int DriverSimulatedJitter = 0;
        public int DriverSimulatedDrop = 0;
        public ApplyMode DriverSimulatorPacketMode = ApplyMode.AllPackets;
        public int DriverMaxMessageSize = NetworkParameterConstants.MaxMessageSize;
        public int DriverReliablePipelineWindowSize = 32;
        public int UseMultipleDrivers = 0;
        public int DriverFragmentedPayloadCapacity = 16 * 1024;
        public int UseFakeSocketConnection = 1;
        private int WorldCreationIndex = 0;
        public NetCodeConfig m_OldGlobalConfig;
        List<NetCodeConfig> m_OldConfigsList = new();
        NetCodeConfig m_GlobalConfigForTests;

        public int[] DriverFuzzFactor;
        public int DriverFuzzOffset = 0;
        public uint DriverRandomSeed = 0;

        private bool m_IsFirstTimeTicking = true;
        internal bool m_IncludeNetcodeSystems;

#if UNITY_EDITOR
        private List<GameObject> m_GhostCollection;
        private BlobAssetStore m_BlobAssetStore;
#endif

        public bool AlwaysDispose;
        ITestWorldStrategy m_WorldStrategy;

        /// <summary>
        /// 配置测试日志，并应用 <see cref="LogLevel"/> 和 <see cref="DebugPackets"/>
        /// </summary>
        /// <param name="world">应用该配置的 World</param>
        private void SetupNetDebugConfig(World world)
        {
            var shouldLog = (world.IsServer() && EnableLogsOnServer) || (world.IsClient() && EnableLogsOnClients);
            world.EntityManager.CreateSingleton(new NetCodeDebugConfig
            {
                // 通过仅保留 Exception 级别近似禁用该 World 的日志，此 Logger 正常情况下不应收到异常
                LogLevel = shouldLog ? LogLevel : NetDebug.LogLevelType.Exception,
                DumpPackets = DebugPackets,
            });
        }

        public NetCodeTestWorld(bool useGlobalConfig=false, double initialElapsedTime = 42)
        {
#if UNITY_EDITOR

    #if UNITY_SERVER
            Debug.Log("WARNING: Your editor target is Server, some netcode tests (especially those involving connection) may fail!");
    #endif
            // 缺少默认 World 会导致 RegisterUnloadOrPlayModeChangeShutdown 未被调用并产生内存泄漏
            DefaultWorldInitialization.DefaultLazyEditModeInitialize();
#endif
            m_OldConfigsList.AddRange(Resources.FindObjectsOfTypeAll<NetCodeConfig>());
            m_OldGlobalConfig = NetCodeConfig.Global;
            m_GlobalConfigForTests = useGlobalConfig ? ScriptableObject.CreateInstance<NetCodeConfig>() : null;
            NetCodeConfig.Global = m_GlobalConfigForTests;

            if (m_GlobalConfigForTests != null)
            {
                if (m_OldGlobalConfig != null) m_OldGlobalConfig.IsGlobalConfig = false;
                m_GlobalConfigForTests.IsGlobalConfig = true;
            }

            m_OldBootstrapAutoConnectPort = ClientServerBootstrap.AutoConnectPort;
            ClientServerBootstrap.AutoConnectPort = 0;
            m_ElapsedTime = initialElapsedTime;
#if !UNITY_SERVER || UNITY_EDITOR
            m_ElapsedTimeClient = initialElapsedTime;
            TickIndex = -1;
            ClientTickIndex = -1;
#endif
            NetworkTimeSystem.ResetFixedTime();
        }

        public void Dispose()
        {
            // 销毁测试期间生成且测试开始前不存在的配置
            foreach (var config in Resources.FindObjectsOfTypeAll<NetCodeConfig>())
            {
                if (!m_OldConfigsList.Contains(config))
                {
                    Object.DestroyImmediate(config, allowDestroyingAssets: true);
                }
            }
            m_WorldStrategy.Dispose();
            if (m_ClientWorlds != null)
            {
                for (int i = 0; i < m_ClientWorlds.Count; ++i)
                {
                    m_WorldStrategy.DisposeClientWorld(m_ClientWorlds[i]);
                }
            }

            if (m_ServerWorld != null && m_ServerWorld.IsCreated) // Host World 可能已在上方随客户端列表释放
            {
                //Assert.That(m_ServerWorld.IsHost(), Is.Not.True, "sanity check failed! world should already have been disposed above");
                m_WorldStrategy.DisposeServerWorld(m_ServerWorld);
            }

            m_ClientWorlds = null;
            m_ServerWorld = null;
            ClientServerBootstrap.AutoConnectPort = m_OldBootstrapAutoConnectPort;

#if UNITY_EDITOR
            if (m_GhostCollection != null)
                m_BlobAssetStore.Dispose();
#endif
            if (m_GlobalConfigForTests)
                UnityEngine.Object.DestroyImmediate(m_GlobalConfigForTests, allowDestroyingAssets:true);
            if (m_OldGlobalConfig != null)
                m_OldGlobalConfig.IsGlobalConfig = true;
            NetCodeConfig.Global = m_OldGlobalConfig;
            m_GlobalConfigForTests = null;
            m_OldGlobalConfig = null;

            // 最终检查并强制清理测试遗留的网络 World
            {
                List<World> toForceCleanup = new();
                foreach (var world in World.All)
                {
                    if (world.IsClient() || world.IsServer() || world.IsThinClient())
                    {
                        Debug.LogError($"world {world} wasn't cleaned up, force cleaning now");
                        toForceCleanup.Add(world);
                    }
                }

                foreach (var world in toForceCleanup)
                {
                    world.Dispose();
                }

                if (toForceCleanup.Count > 0)
                {
                    Assert.Fail("world cleanup issue, sanity check failed");
                }
            }
        }

        public void DisposeAllClientWorlds()
        {
            for (int i = 0; i < m_ClientWorlds.Count; ++i)
            {
                if (m_ClientWorlds[i].IsHost()) continue; // Host World 由服务端清理流程释放
                m_WorldStrategy.DisposeClientWorld(m_ClientWorlds[i]);
            }

            m_ClientWorlds = null;
        }

        public void DisposeServerWorld()
        {
            m_WorldStrategy.DisposeServerWorld(m_ServerWorld);
            m_ServerWorld = null;
        }

        public void DisposeDefaultWorld()
        {
            m_WorldStrategy.DisposeDefaultWorld();
        }

        public void SetServerTick(NetworkTick tick)
        {
            var ent = TryGetSingletonEntity<NetworkTime>(m_ServerWorld);
            var networkTime = m_ServerWorld.EntityManager.GetComponentData<NetworkTime>(ent);
            networkTime.ServerTick = tick;
            m_ServerWorld.EntityManager.SetComponentData(ent, networkTime);
        }

        public NetworkTime GetNetworkTime(World world)
        {
            var ent = TryGetSingletonEntity<NetworkTime>(world);
            return world.EntityManager.GetComponentData<NetworkTime>(ent);
        }

        internal static IReadOnlyList<Type> s_AllClientSystems;
        internal static IReadOnlyList<Type> s_AllThinClientSystems;
        internal static IReadOnlyList<Type> s_AllServerSystems;
        internal static IReadOnlyList<Type> s_AllHostSystems;

        internal static List<Type> m_ControlSystems;
        internal static List<Type> m_ClientSystems;
        internal static List<Type> m_ThinClientSystems;
        internal static List<Type> m_ServerSystems;
        internal static List<Type> m_HostSystems;
        internal static List<Type> m_BakingSystems;

        public List<string> TestSpecificAdditionalAssemblies = new List<string>(8);

        int m_NumClients = 0;

        private static bool IsFromNetCodeAssembly(Type sys)
        {
            return sys.Assembly.FullName.StartsWith("Unity.NetCode,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.Entities,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.Transforms,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.Scenes,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.NetCode.EditorTests,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.NetCode.Physics.EditorTests,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.NetCode.TestsUtils,", StringComparison.Ordinal) ||
                typeof(IGhostComponentSerializerRegistration).IsAssignableFrom(sys);
        }

        private bool IsFromTestSpecificAdditionalAssembly(Type sys)
        {
            var sysAssemblyFullName = sys.Assembly.FullName;
            foreach (var extraNetcodeAssembly in TestSpecificAdditionalAssemblies)
            {
                if (sysAssemblyFullName.StartsWith(extraNetcodeAssembly, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public void Bootstrap(bool includeNetCodeSystems, params Type[] userSystems)
        {
            Bootstrap(includeNetCodeSystems, false, userSystems);
        }

        public void Bootstrap(bool includeNetCodeSystems, bool useNormalMainLoop, params Type[] userSystems)
        {
#if UNITY_6000_0_OR_NEWER
            if (useNormalMainLoop)
            {
                m_WorldStrategy = new PlayModeTestWorldStrategy();
            }
            else
            {
                m_WorldStrategy = new EditModeTestWorldStrategy();
            }
#else
            if (useNormalMainLoop)
                Assert.Fail("PlayModeTestWorldStrategy is only supported in 6000.0 and newer, use EditModeTestWorldStrategy instead.");
            m_WorldStrategy = new EditModeTestWorldStrategy();
#endif


            m_WorldStrategy.Bootstrap(this);

            m_IncludeNetcodeSystems = includeNetCodeSystems;

            m_ControlSystems = new List<Type>(256);
            m_ClientSystems = new List<Type>(256);
            m_ThinClientSystems = new List<Type>(256);
            m_ServerSystems = new List<Type>(256);
            m_HostSystems = new List<Type>(256);
            m_BakingSystems = new List<Type>(256);
#if !UNITY_SERVER || UNITY_EDITOR
            m_ControlSystems.Add(typeof(TickClientInitializationSystem));
            m_ControlSystems.Add(typeof(TickClientSimulationSystem));
            m_ControlSystems.Add(typeof(TickClientPresentationSystem));
#endif
#if !UNITY_CLIENT || UNITY_EDITOR
            m_ControlSystems.Add(typeof(TickServerInitializationSystem));
            m_ControlSystems.Add(typeof(TickServerSimulationSystem));
#endif
            m_ControlSystems.Add(typeof(DriverMigrationSystem));

            s_AllClientSystems ??= DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.ClientSimulation);
            s_AllThinClientSystems ??= DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.ThinClientSimulation);
            s_AllServerSystems ??= DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.ServerSimulation);
            s_AllHostSystems ??= DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation);

            bool IncludeNetcodeSystemsFilter(Type x) => IsFromNetCodeAssembly(x) || IsFromTestSpecificAdditionalAssembly(x);

            Func<Type, bool> filter = includeNetCodeSystems
                ? IncludeNetcodeSystemsFilter
                : IsFromTestSpecificAdditionalAssembly;
            m_IncludeNetcodeSystems = includeNetCodeSystems;

            m_ClientSystems.AddRange(s_AllClientSystems.Where(filter));
            m_ThinClientSystems.AddRange(s_AllThinClientSystems.Where(filter));
            m_ServerSystems.AddRange(s_AllServerSystems.Where(filter));
            m_HostSystems.AddRange(s_AllHostSystems.Where(filter));

            m_ClientSystems.Add(typeof(Unity.Entities.UpdateWorldTimeSystem));
            m_ThinClientSystems.Add(typeof(Unity.Entities.UpdateWorldTimeSystem));
            m_ServerSystems.Add(typeof(Unity.Entities.UpdateWorldTimeSystem));
            m_HostSystems.Add(typeof(Unity.Entities.UpdateWorldTimeSystem));

            foreach (var sys in userSystems)
            {
                var flags = WorldSystemFilterFlags.Default;
                var attrs = TypeManager.GetSystemAttributes(sys, typeof(WorldSystemFilterAttribute));
                if (attrs != null && attrs.Length == 1)
                    flags = ((WorldSystemFilterAttribute) attrs[0]).FilterFlags;
                var grp = sys;
                while ((flags & WorldSystemFilterFlags.Default) != 0)
                {
                    attrs = TypeManager.GetSystemAttributes(grp, typeof(UpdateInGroupAttribute));
                    if (attrs != null && attrs.Length == 1)
                        grp = ((UpdateInGroupAttribute) attrs[0]).GroupType;
                    else
                    {
                        flags &= ~WorldSystemFilterFlags.Default;
                        flags |= WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation;
                        break;
                    }

                    attrs = TypeManager.GetSystemAttributes(grp, typeof(WorldSystemFilterAttribute));
                    if (attrs != null && attrs.Length == 1)
                    {
                        flags &= ~WorldSystemFilterFlags.Default;
                        flags |= ((WorldSystemFilterAttribute) attrs[0]).ChildDefaultFilterFlags;
                    }
                }

                if ((flags & WorldSystemFilterFlags.ClientSimulation) != 0)
                    m_ClientSystems.Add(sys);
                if ((flags & WorldSystemFilterFlags.ThinClientSimulation) != 0)
                    m_ThinClientSystems.Add(sys);
                if ((flags & WorldSystemFilterFlags.ServerSimulation) != 0)
                    m_ServerSystems.Add(sys);
                if (((flags & WorldSystemFilterFlags.ClientSimulation) != 0) || ((flags & WorldSystemFilterFlags.ServerSimulation) != 0))
                    m_HostSystems.Add(sys);
                if ((flags & WorldSystemFilterFlags.BakingSystem) != 0)
                    m_BakingSystems.Add(sys);
            }
        }

        public void CreateAdditionalClientWorlds(int numClients, bool tickWorldAfterCreation = true, bool useThinClients = false)
        {
            CreateWorlds(false, numClients, tickWorldAfterCreation, useThinClients, throwIfWorldsAlreadyExist: false);
        }

        public void CreateWorlds(bool server, int numClients, bool tickWorldAfterCreation = true,
            bool useThinClients = false, bool throwIfWorldsAlreadyExist = true, int numHostWorlds = 0)
        {
            CreateWorldsAsync(server, numClients, tickWorldAfterCreation, useThinClients, throwIfWorldsAlreadyExist, numHostWorlds: numHostWorlds).Wait();
        }

        public async Task CreateWorldsAsync(bool server, int numClients, bool tickWorldAfterCreation = true, bool useThinClients = false, bool throwIfWorldsAlreadyExist = true, int numHostWorlds = 0)
        {
            m_NumClients += numClients;
            var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
            NetworkStreamReceiveSystem.DriverConstructor = this;
#if UNITY_EDITOR || NETCODE_DEBUG
            var oldDebugPort = GhostStatsConnection.Port;
            GhostStatsConnection.Port = 0;
#endif
            if (!m_DefaultWorldInitialized)
            {
                TypeManager.SortSystemTypesInCreationOrder(m_ControlSystems); // 确保遵循 CreationOrder
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(m_DefaultWorld,
                    m_ControlSystems);
                m_DefaultWorldInitialized = true;
            }

            var testMethodName = NUnit.Framework.TestContext.CurrentContext.Test.MethodName;

            if (server)
            {
                if (m_ServerWorld != null && throwIfWorldsAlreadyExist)
                    throw new InvalidOperationException("Server world already created");
                m_ServerWorld = m_WorldStrategy.CreateServerWorld($"ServerTest-{testMethodName}");
                SetupNetDebugConfig(m_ServerWorld);
            }

            // 在普通客户端之前创建 Host，确保 Tick 时始终先更新 Server/Host，再更新客户端
            if (numHostWorlds > 0)
            {
                if (numHostWorlds > 1) throw new NotImplementedException();
                // TODO: 支持单个测试中多次调用此流程
                // Host World 不加入客户端列表，以保证额外客户端可按确定顺序访问
                // 如果列表包含 Host，testWorld.ClientWorlds[0] 的含义将不再稳定，因此当前仅将 Host 视为服务端 World
                m_ServerWorld = (m_WorldStrategy.CreateHostWorld($"ServerTest-{testMethodName}"));
                SetupNetDebugConfig(m_ServerWorld);
            }

            if (numClients > 0)
            {
                var oldSize = m_ClientWorlds?.Count ?? 0;
                var newSize = oldSize + numClients;

                if (m_ClientWorlds != null && !m_ClientWorlds[0].IsHost() && throwIfWorldsAlreadyExist)
                    throw new InvalidOperationException("Client worlds already created");
                if (m_ClientWorlds == null)
                    m_ClientWorlds = new List<World>(newSize);

                WorldCreationIndex = 0;
                for (int i = oldSize; i < newSize; ++i)
                {
                    try
                    {
                        WorldCreationIndex = i;

                        m_ClientWorlds.Add(m_WorldStrategy.CreateClientWorld($"ClientTest{i}-{testMethodName}", useThinClients));

                        SetupNetDebugConfig(m_ClientWorlds[i]);
                    }
                    catch (Exception e)
                    {
                        m_ClientWorlds = null;
                        Debug.LogException(e);
                        throw;
                    }
                }
            }

#if UNITY_EDITOR || NETCODE_DEBUG
            GhostStatsConnection.Port = oldDebugPort;
#endif
            NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;

            // 必须在首次 Tick 前完成日志抑制配置
            TrySuppressNetDebug(true, true, hasServer: server || numHostWorlds > 0);

            // 运行一次 Tick，使 Ghost Collection 相关系统和组件完成首次更新
            if (tickWorldAfterCreation)
                await TickAsync();

        }

        /// <summary>
        /// 通过 NetDebug 抑制指定的 NetCode 警告
        /// </summary>
        /// <param name="suppressRunInBackground">
        /// CI 环境会因 runInBackground=false 警告导致测试失败，因此需要抑制该警告
        /// 如果不存在 NetCode 系统，即没有 NetDebug，则无需抑制
        /// </param>
        /// <param name="suppressApprovalRpc">
        /// 此日志在 RPC 测试中可能大量重复并压垮 Logger，因此默认抑制
        /// </param>
        /// <remarks>部分测试会在建立 Collection 前暂停 Tick，因此此方法可能被多次调用</remarks>
        public bool TrySuppressNetDebug(bool suppressRunInBackground, bool suppressApprovalRpc, bool hasServer = true)
        {
            var success = true;
            if (hasServer && (AlwaysDispose || ServerWorld.IsCreated))
            {
                success &= TryGetSingletonEntity<NetDebug>(ServerWorld) != default;
                if (success)
                {
                    ref var netDebug = ref GetSingletonRW<NetDebug>(ServerWorld).ValueRW;
                    netDebug.SuppressApplicationRunInBackgroundWarning = suppressRunInBackground;
                    netDebug.SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning = suppressApprovalRpc;
                }
            }

            if (ClientWorlds != null)
            {
                foreach (var clientWorld in ClientWorlds)
                {
                    if (AlwaysDispose || clientWorld.IsCreated)
                    {
                        var foundClient = TryGetSingletonEntity<NetDebug>(clientWorld) != Entity.Null;
                        if (foundClient)
                        {
                            ref var netDebug = ref GetSingletonRW<NetDebug>(clientWorld).ValueRW;
                            netDebug.SuppressApplicationRunInBackgroundWarning = suppressRunInBackground;
                            netDebug.SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning = suppressApprovalRpc;
                        }
                        success &= foundClient;
                    }
                }
            }
            return success;
        }
#if UNITY_EDITOR || NETCODE_DEBUG
        public Entity TryCreateGhostMetricsSingleton(World world)
        {
            if (!world.EntityManager.CreateEntityQuery(typeof(GhostMetricsMonitor)).TryGetSingletonEntity<GhostMetricsMonitor>(out var singletonEntity))
            {
                singletonEntity = world.EntityManager.CreateEntity(
                    typeof(GhostMetricsMonitor),
                    typeof(SnapshotMetrics),
                    typeof(GhostMetrics),
                    typeof(GhostNames),
                    typeof(PredictionErrorNames)
                );
            }
            return singletonEntity;
        }
#endif

        public void ApplyDT(float dt)
        {
            ApplyDTServer(dt);
            ApplyDTClient(dt);
        }

        public void ApplyDTServer(float dt)
        {
            ++TickIndex;
            //Debug.Log($"[{TickIndex}]: TICK");
            if (m_IsFirstTimeTicking)
            {
                // 模拟时间系统首次更新时的逻辑
                m_IsFirstTimeTicking = false;
                m_ElapsedTime = -dt;
            }

            // 在网络时间系统中使用固定时间步，避免测试依赖真实时间
            m_ElapsedTime += dt;
            m_DefaultWorld.SetTime(new TimeData(m_ElapsedTime, dt));
            if (m_ServerWorld != null)
            {
                m_ServerWorld.SetTime(new TimeData(m_ElapsedTime, dt));
            }
        }

        private bool m_IsFirstTimeTickingClient = true;
        private double m_ElapsedTimeClient = 0;
        public static int ClientTickIndex { get; private set; }

        public void ApplyDTClient(float dt)
        {
            ++ClientTickIndex;
            if (m_IsFirstTimeTickingClient)
            {
                m_IsFirstTimeTickingClient = false;
                m_ElapsedTimeClient = -dt;
            }

            NetworkTimeSystem.s_FixedTimestampMS += (uint) (dt * 1000.0f);
            m_ElapsedTimeClient += dt;

            if (m_ClientWorlds != null)
            {
                foreach (var clientWorld in m_ClientWorlds)
                {
                    if (clientWorld.IsCreated)
                        clientWorld.SetTime(new TimeData(m_ElapsedTimeClient, dt));
                }
            }
        }

        const float k_defaultDT = 1f / 60f;

        public void Tick(float dt = k_defaultDT)
        {
            m_WorldStrategy.TickNoAwait(dt);
        }

        public void TickMultiple( int numTicks, float dt = k_defaultDT)
        {
            for ( int t=0; t<numTicks; ++t )
            {
                Tick(dt);
            }
        }

        /// <summary>
        /// 执行包含全部系统的完整引擎帧
        /// 默认运行到 EndOfFrame，而 yield return null 只运行到 Update 之后
        /// 在 Edit Mode 测试中会直接 Tick NetCodeTestWorld 且不让出帧，因此并非真正异步
        /// </summary>
        /// <param name="dt"></param>
        /// <param name="waitInstruction">默认执行完整帧直到 EndOfFrame，如需停在帧内其他阶段，请传入对应的 Awaitable 等待指令</param>
        /// <returns></returns>
        public async Task TickAsync(float dt=k_defaultDT, NetcodeAwaitable waitInstruction = null)
        {
            await m_WorldStrategy.TickAsync(dt, waitInstruction);
        }

        public async Task TickMultipleAsync(int count, float dt = k_defaultDT, NetcodeAwaitable waitInstruction = null)
        {
            for (int i = 0; i < count; i++)
            {
                await TickAsync(dt, waitInstruction);
            }
        }

        public void TickServerWorld(float dt = k_defaultDT)
        {
            m_WorldStrategy.TickServerWorld(dt);
        }

#if !UNITY_SERVER || UNITY_EDITOR

        // 与 Tick 类似，但只更新客户端 World
        // 用于在不更新服务端的情况下执行部分 Tick，或精确控制服务端与客户端之间的更新时序
        public void TickClientWorld(float dt = k_defaultDT)
        {
            m_WorldStrategy.TickClientWorld(dt);
        }
#endif

        public void MigrateServerWorld(World suppliedWorld = null)
        {
            DriverMigrationSystem migrationSystem = default;

            foreach (var world in World.All)
            {
                if ((migrationSystem = world.GetExistingSystemManaged<DriverMigrationSystem>()) != null)
                    break;
            }

            var ticket = migrationSystem.StoreWorld(ServerWorld);
            DisposeServerWorld();

            Assert.True(suppliedWorld == null || suppliedWorld.IsServer());
            var newWorld = migrationSystem.LoadWorld(ticket, suppliedWorld);
            m_ServerWorld = m_WorldStrategy.CreateServerWorld(newWorld.Name, newWorld);

            Assert.True(newWorld.Name == m_ServerWorld.Name);

            TrySuppressNetDebug(true, true);
        }

        public void MigrateClientWorld(int index, World suppliedWorld = null)
        {
            if (index > ClientWorlds.Length)
                throw new IndexOutOfRangeException($"ClientWorlds only contain {ClientWorlds.Length} items, you are trying to read index {index} that is out of range.");

            DriverMigrationSystem migrationSystem = default;

            foreach (var world in World.All)
            {
                if ((migrationSystem = world.GetExistingSystemManaged<DriverMigrationSystem>()) != null)
                    break;
            }

            var ticket = migrationSystem.StoreWorld(ClientWorlds[index]);
            m_WorldStrategy.DisposeClientWorld(ClientWorlds[index]);

            var newWorld = migrationSystem.LoadWorld(ticket, suppliedWorld);
            m_ClientWorlds[index] = m_WorldStrategy.CreateClientWorld(newWorld.Name, false, newWorld);

            Assert.True(newWorld.Name == m_ClientWorlds[index].Name);

            TrySuppressNetDebug(true, true);
        }

        public World CreateServerWorld(string name, World world = null)
        {
            return m_WorldStrategy.CreateServerWorld(name, world);
        }

        public void RestartClientWorld(int index)
        {
            var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
            NetworkStreamReceiveSystem.DriverConstructor = this;

            var name = m_ClientWorlds[index].Name;
            m_WorldStrategy.DisposeClientWorld(ClientWorlds[index]);

            m_ClientWorlds[index] = m_WorldStrategy.CreateClientWorld(name, false);
            SetupNetDebugConfig(ClientWorlds[index]);
            NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;
        }

        public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            // 实际连接基于 IPC，但默认将连接类型强制标记为 Socket
            // 这样可以禁用专门针对 IPC 的检查和优化，使测试覆盖更通用的连接路径
            // 可通过 UseFakeSocketConnection 关闭此行为
            var transportType = UseFakeSocketConnection == 1 ? TransportType.Socket : TransportType.IPC;

            var networkSettings = GetClientNetworkSettings(world, out int fuzzFactor);
            var driverInstance = new NetworkDriverStore.NetworkDriverInstance();
            if (UseMultipleDrivers == 0)
                driverInstance.driver = NetworkDriver.Create(new IPCNetworkInterface(), networkSettings);
            else
            {
                if ((WorldCreationIndex & 0x1) == 0)
                {
                    driverInstance.driver = NetworkDriver.Create(new IPCNetworkInterface(), networkSettings);
                }
                else
                {
                    driverInstance.driver = NetworkDriver.Create(new UDPNetworkInterface(), networkSettings);
                }
            }

            // 即使传输介质为 IPC，也按 Socket 类型注册 Driver
            if (DriverSimulatedDelay + fuzzFactor > 0)
            {
                DefaultDriverBuilder.CreateClientSimulatorPipelines(ref driverInstance);
                driverStore.RegisterDriver(transportType, driverInstance);
            }
            else
            {
                DefaultDriverBuilder.CreateClientPipelines(ref driverInstance);
                driverStore.RegisterDriver(transportType, driverInstance);
            }
        }

        public NetworkSettings GetClientNetworkSettings(World world, out int fuzzFactor)
        {
            var packetDelay = DriverSimulatedDelay;
            int networkRate = 60;

            // 按最大延迟缓存每帧的三类数据包，并将容量加倍作为安全余量
            int maxPackets = 2 * (networkRate * 3 * (packetDelay + DriverSimulatedJitter) + 999) / 1000;

            fuzzFactor = 0;
            // World 命名格式为 ClientTestXX-NameOfTest，此处提取其中的 XX
            var worldId = CalculateWorldId(world);
            if (DriverFuzzFactor?.Length >= worldId + 1)
            {
                fuzzFactor = DriverFuzzFactor[worldId];
            }

            var simParams = new SimulatorUtility.Parameters
            {
                Mode = DriverSimulatorPacketMode,
                MaxPacketSize = NetworkParameterConstants.MTU,
                MaxPacketCount = maxPackets,
                PacketDelayMs = packetDelay,
                PacketJitterMs = DriverSimulatedJitter,
                PacketDropInterval = DriverSimulatedDrop,
                FuzzFactor = fuzzFactor,
                FuzzOffset = DriverFuzzOffset,
                RandomSeed = DriverRandomSeed
            };
            var networkSettings = new NetworkSettings();
            networkSettings
                .WithReliableStageParameters(windowSize:DriverReliablePipelineWindowSize)
                .WithFragmentationStageParameters(payloadCapacity:DriverFragmentedPayloadCapacity)
                .WithNetworkConfigParameters
                (
                    maxFrameTimeMS: MaxFrameTime,
                    fixedFrameTimeMS: DriverFixedTime,
                    connectTimeoutMS: ConnectTimeout,
                    maxConnectAttempts: MaxConnectAttempts,
                    maxMessageSize: DriverMaxMessageSize
                );
            if (DriverFuzzFactor != null ||
                DriverSimulatedJitter != 0 ||
                DriverSimulatedDrop != 0 ||
                DriverSimulatedDelay != 0)
                networkSettings.AddRawParameterStruct(ref simParams);

            return networkSettings;
        }

        public static int CalculateWorldId(World world)
        {
            var regex = new Regex(@"(ClientTest)(\d)", RegexOptions.Singleline);
            var match = regex.Match(world.Name);
            return int.Parse(match.Groups[2].Value);
        }

        public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            var networkSettings = GetServerNetworkSettings();
            var driverInstance = new NetworkDriverStore.NetworkDriverInstance();
            driverInstance.driver = NetworkDriver.Create(new IPCNetworkInterface(), networkSettings);
            DefaultDriverBuilder.CreateServerPipelines(ref driverInstance);
            driverStore.RegisterDriver(TransportType.IPC, driverInstance);
            if (UseMultipleDrivers != 0)
            {
                var socketInstance = new NetworkDriverStore.NetworkDriverInstance();
                socketInstance.driver = NetworkDriver.Create(new UDPNetworkInterface(), networkSettings);
                DefaultDriverBuilder.CreateServerPipelines(ref socketInstance);
                driverStore.RegisterDriver(TransportType.Socket, socketInstance);
            }
        }

        public NetworkSettings GetServerNetworkSettings()
        {
            var networkSettings = new NetworkSettings();
            networkSettings
                .WithReliableStageParameters(windowSize: DriverReliablePipelineWindowSize)
                .WithFragmentationStageParameters(payloadCapacity:DriverFragmentedPayloadCapacity)
                .WithNetworkConfigParameters(
                    maxFrameTimeMS: MaxFrameTime,
                    fixedFrameTimeMS: DriverFixedTime,
                    maxMessageSize: DriverMaxMessageSize
                );

            return networkSettings;
        }

        public void Connect(float dt = k_defaultDT, int maxSteps = 7, bool failTestIfConnectionFails = true, bool tickUntilConnected = true, bool enableGhostReplication = false, bool withConnectionState = false)
        {
            ConnectAsync(dt, maxSteps, failTestIfConnectionFails, tickUntilConnected, enableGhostReplication: enableGhostReplication, withConnectionState: withConnectionState).Wait();
        }

        /// <summary>
        /// 建立连接，连接失败时抛出异常
        /// </summary>
        public async Task ConnectAsync(float dt = k_defaultDT, int maxSteps = 7, bool failTestIfConnectionFails = true, bool tickUntilConnected = true, bool enableGhostReplication = false, bool withConnectionState = false)
        {
            var ep = NetworkEndpoint.LoopbackIpv4;
            ep.Port = 7979;
            Debug.Assert(GetSingletonRW<NetworkStreamDriver>(ServerWorld).ValueRW.Listen(ep), $"[{ServerWorld.Name}] Listen failed during Connect!");
            if ((ClientWorlds == null || ClientWorlds.Length == 0) && !ServerWorld.IsHost())
                throw new InvalidOperationException("If the server isn't a host, there should be other client worlds to connect to!");
            if (ServerWorld.IsHost())
                Tick(dt);
            if (ClientWorlds == null || ClientWorlds.Length == 0)
                return;
            var connectionEntities = new Entity[ClientWorlds.Length];
            for (int i = 0; i < ClientWorlds.Length; ++i)
            {
                if (ClientWorlds[i].IsHost())
                {
                    Tick(dt); // 执行一次 Tick，使连接状态实际完成更新
                    continue;
                }

                Entity entityToUse;
                if (withConnectionState)
                    entityToUse = ClientWorlds[i].EntityManager.CreateEntity(typeof(ConnectionState));
                else
                    entityToUse = default;

                connectionEntities[i] = GetSingletonRW<NetworkStreamDriver>(ClientWorlds[i]).ValueRW.Connect(ClientWorlds[i].EntityManager, ep, ent: entityToUse);
            }

            int stepsLeft = maxSteps;
            if (tickUntilConnected)
            {
                for (int i = 0; i < ClientWorlds.Length; ++i)
                {
                    if (ClientWorlds[i].IsHost()) continue;
                    await TickUntilConnectedAsync(ClientWorlds[i], dt, stepsLeft, connectionEntities[i], failTestIfConnectionFails);
                }
            }

            if (enableGhostReplication)
            {
                GoInGame();
            }
        }

        public void TickUntilConnected(World world, float dt = k_defaultDT, int maxSteps = 7, Entity connectionEntity = default, bool failTestIfConnectionFails = true)
        {
            TickUntilConnectedAsync(world, dt, maxSteps, connectionEntity, failTestIfConnectionFails).Wait();
        }

        public async Task TickUntilConnectedAsync(World world, float dt = k_defaultDT, int maxSteps = 7, Entity connectionEntity = default, bool failTestIfConnectionFails = true)
        {
            var initialMaxSteps = maxSteps;
            int stepsLeft = maxSteps;

            while (TryGetSingletonEntity<NetworkId>(world) == Entity.Null)
            {
                if (stepsLeft <= 0)
                {
                    var streamDriver = GetSingleton<NetworkStreamDriver>(world);
                    if (failTestIfConnectionFails)
                    {
                        string connectionState = "No_NetworkConnection_Entity_Left";
                        if (world.EntityManager.Exists(connectionEntity))
                        {
                            var nsc = world.EntityManager.GetComponentData<NetworkStreamConnection>(connectionEntity);
                            connectionState = $"{connectionEntity.ToFixedString()} NetworkStreamConnection[{nsc.Value.ToFixedString()}-{nsc.CurrentState.ToString()}]";
                        }
                        Assert.Fail($"ClientWorld[{world.Name}] failed to connect to the server after {initialMaxSteps} ticks! Driver status: {connectionState}!");
                    }

                    return;
                }
                --stepsLeft;
                await m_WorldStrategy.TickAsync(dt);
            }
        }

        public void StartSeverListen()
        {
            var ep = NetworkEndpoint.LoopbackIpv4;
            ep.Port = 7979;
            Debug.Assert(GetSingletonRW<NetworkStreamDriver>(ServerWorld).ValueRW.Listen(ep), $"[{ServerWorld.Name}] Listen failed during Connect!");
        }

        public void ConnectSingleClientWorld(int clientIndex, float dt = NetCodeTestWorld.k_defaultDT, int maxSteps = 7, bool failTestIfConnectionFails = true)
        {
            Assert.True( clientIndex < ClientWorlds.Length );
            var ep = NetworkEndpoint.LoopbackIpv4;
            ep.Port = 7979;

            var connectionEntity = GetSingletonRW<NetworkStreamDriver>(ClientWorlds[clientIndex]).ValueRW.Connect(ClientWorlds[clientIndex].EntityManager, ep);

            int stepsLeft = maxSteps;

            while (TryGetSingletonEntity<NetworkId>(ClientWorlds[clientIndex]) == Entity.Null)
            {
                if (stepsLeft <= 0)
                {
                    var streamDriver = GetSingleton<NetworkStreamDriver>(ClientWorlds[clientIndex]);
                    if (failTestIfConnectionFails)
                    {
                        string connectionState = "No_NetworkConnection_Entity_Left";
                        if (ClientWorlds[clientIndex].EntityManager.Exists(connectionEntity))
                        {
                            var nsc = ClientWorlds[clientIndex].EntityManager.GetComponentData<NetworkStreamConnection>(connectionEntity);
                            connectionState = $"{connectionEntity.ToFixedString()} NetworkStreamConnection[{nsc.Value.ToFixedString()}-{nsc.CurrentState.ToString()}]";
                        }
                        Assert.Fail($"ClientWorld {clientIndex}{ClientWorlds[clientIndex].Name} failed to connect to the server after {maxSteps} ticks! Driver status: {connectionState}!");
                    }
                    return;
                }
                --stepsLeft;
                Tick(dt);
            }
        }


        public void GoInGame(World w = null)
        {
            if (w == null)
            {
                if (ServerWorld != null)
                {
                    GoInGame(ServerWorld);
                }
                if (ClientWorlds == null) return;
                foreach (var clientWorld in ClientWorlds)
                {
                    GoInGame(clientWorld);
                }

                return;
            }

            var type = ComponentType.ReadOnly<NetworkId>();
            using var query = w.EntityManager.CreateEntityQuery(type);
            var connections = query.ToEntityArray(Allocator.Temp);
            foreach (var connection in connections)
            {
                w.EntityManager.AddComponentData(connection, new NetworkStreamInGame());
            }

            connections.Dispose();
        }

        public void ExitFromGame()
        {
            void RemoveTag(World world)
            {
                var type = ComponentType.ReadOnly<NetworkId>();
                using var query = world.EntityManager.CreateEntityQuery(type);
                var connections = query.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < connections.Length; ++i)
                {
                    world.EntityManager.RemoveComponent<NetworkStreamInGame>(connections[i]);
                }

                connections.Dispose();
            }

            RemoveTag(ServerWorld);
            for (int i = 0; i < ClientWorlds.Length; ++i)
            {
                RemoveTag(ClientWorlds[i]);
            }
        }

        public void SetInGame(int client)
        {
            var type = ComponentType.ReadOnly<NetworkId>();
            using var clientQuery = ClientWorlds[client].EntityManager.CreateEntityQuery(type);
            var clientEntity = clientQuery.ToEntityArray(Allocator.Temp);
            ClientWorlds[client].EntityManager.AddComponent<NetworkStreamInGame>(clientEntity[0]);
            var clientNetId = ClientWorlds[client].EntityManager.GetComponentData<NetworkId>(clientEntity[0]);
            clientEntity.Dispose();

            using var query = ServerWorld.EntityManager.CreateEntityQuery(type);
            var connections = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < connections.Length; ++i)
            {
                var netId = ServerWorld.EntityManager.GetComponentData<NetworkId>(connections[i]);
                if (netId.Value == clientNetId.Value)
                {
                    ServerWorld.EntityManager.AddComponent<NetworkStreamInGame>(connections[i]);
                    break;
                }
            }

            connections.Dispose();
        }

        public void RemoveFromGame(int client)
        {
            var type = ComponentType.ReadOnly<NetworkId>();
            using var clientQuery = ClientWorlds[client].EntityManager.CreateEntityQuery(type);
            var clientEntity = clientQuery.ToEntityArray(Allocator.Temp);
            ClientWorlds[client].EntityManager.RemoveComponent<NetworkStreamInGame>(clientEntity[0]);
            var clientNetId = ClientWorlds[client].EntityManager.GetComponentData<NetworkId>(clientEntity[0]);
            clientEntity.Dispose();

            using var query = ServerWorld.EntityManager.CreateEntityQuery(type);
            var connections = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < connections.Length; ++i)
            {
                var netId = ServerWorld.EntityManager.GetComponentData<NetworkId>(connections[i]);
                if (netId.Value == clientNetId.Value)
                {
                    ServerWorld.EntityManager.RemoveComponent<NetworkStreamInGame>(connections[i]);
                    break;
                }
            }

            connections.Dispose();
        }

        public Entity TryGetSingletonEntity<T>(World w)
        {
            var type = ComponentType.ReadOnly<T>();
            using var query = w.EntityManager.CreateEntityQuery(type);
            int entCount = query.CalculateEntityCount();
#if UNITY_EDITOR
            if (entCount >= 2)
                Debug.LogError("Trying to get singleton, but there are multiple matching entities");
#endif
            if (entCount != 1)
                return Entity.Null;
            return query.GetSingletonEntity();
        }

        public T GetSingleton<T>(World w) where T : unmanaged, IComponentData
        {
            var type = ComponentType.ReadOnly<T>();
            using var query = w.EntityManager.CreateEntityQuery(type);
            return query.GetSingleton<T>();
        }

        public RefRW<T> GetSingletonRW<T>(World w) where T : unmanaged, IComponentData
        {
            var type = ComponentType.ReadWrite<T>();
            using var query = w.EntityManager.CreateEntityQuery(type);
            return query.GetSingletonRW<T>();
        }

        public DynamicBuffer<T> GetSingletonBuffer<T>(World w) where T : unmanaged, IBufferElementData
        {
            var type = ComponentType.ReadOnly<T>();
            using var query = w.EntityManager.CreateEntityQuery(type);
            return query.GetSingletonBuffer<T>();
        }

#if UNITY_EDITOR
        public bool CreateGhostCollection(params GameObject[] ghostTypes)
        {
            if (m_GhostCollection != null)
                return false;
            m_GhostCollection = new List<GameObject>(ghostTypes.Length);

            foreach (var ghostObject in ghostTypes)
            {
                var ghost = ghostObject.GetComponent<GhostAuthoringComponent>();
                if (ghost == null)
                {
                    ghost = ghostObject.AddComponent<GhostAuthoringComponent>();
                }
                ghost.prefabId = Guid.NewGuid().ToString().Replace("-", "");
                m_GhostCollection.Add(ghostObject);
            }
            m_BlobAssetStore = new BlobAssetStore(128);
            return true;
        }

        public Entity SpawnOnServer(int prefabIndex)
        {
            if (m_GhostCollection == null)
                throw new InvalidOperationException("Cannot spawn ghost on server without setting up the ghost first");
            var prefabCollection = TryGetSingletonEntity<NetCodeTestPrefabCollection>(ServerWorld);
            if (prefabCollection == Entity.Null)
                throw new InvalidOperationException("Cannot spawn ghost on server if a ghost prefab collection is not created");
            var prefabBuffers = ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabCollection);
            return ServerWorld.EntityManager.Instantiate(prefabBuffers[prefabIndex].Value);
        }

        private Entity BakeGameObject(GameObject go, World world, BlobAssetStore blobAssetStore)
        {
            // BakingUtility.BakeGameObjects 会清理此前烘焙的实体，因此需要使用中间 World
            // 烘焙完成后将实体从中间 World 移入目标 World
            // BakeGameObject 需要返回主烘焙实体，所以通过 EntityGuid 在目标 World 中重新定位该实体
            using var intermediateWorld = new World("NetCodeBakingWorld");

            var bakingSettings = new BakingSettings(BakingUtility.BakingFlags.AddEntityGUID, blobAssetStore);
            bakingSettings.PrefabRoot = go;
            bakingSettings.ExtraSystems.AddRange(m_BakingSystems);
            BakingUtility.BakeGameObjects(intermediateWorld, new GameObject[] {}, bakingSettings);

            var bakingSystem = intermediateWorld.GetExistingSystemManaged<BakingSystem>();
            var intermediateEntity = bakingSystem.GetEntity(go);
            var intermediateEntityGuid = intermediateWorld.EntityManager.GetComponentData<EntityGuid>(intermediateEntity);

            // 复制所有被追踪和烘焙的实体
            // Baker 为该 GameObject 创建的实体都带有 EntityGuid，因此该查询也能覆盖附加实体
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<EntityGuid>().WithOptions(EntityQueryOptions.IncludePrefab);

            using var bakedEntities = intermediateWorld.EntityManager.CreateEntityQuery(builder);
            world.EntityManager.MoveEntitiesFrom(intermediateWorld.EntityManager, bakedEntities);

            // 将中间 World 的 EntityGuid 与目标 World 实体逐一比较，定位主烘焙实体
            using var query = builder.Build(world.EntityManager);
            var entityArray = query.ToEntityArray(Allocator.Temp);
            var entityGUIDs = query.ToComponentDataArray<EntityGuid>(Allocator.Temp);
            for (int index = 0; index < entityGUIDs.Length; ++index)
            {
                if (entityGUIDs[index] == intermediateEntityGuid)
                {
                    return entityArray[index];
                }
            }

            Debug.LogError($"Copied Entity {intermediateEntityGuid} not found");
            return Entity.Null;
        }

        public Entity SpawnOnServer(GameObject go)
        {
            if (m_GhostCollection == null)
                throw new InvalidOperationException("Cannot spawn ghost on server without setting up the ghost first");
            int index = m_GhostCollection.IndexOf(go);
            if (index >= 0)
                return SpawnOnServer(index);

            return BakeGameObject(go, ServerWorld, m_BlobAssetStore);
        }

        public Entity BakeGhostCollection(World world)
        {
            if (m_GhostCollection == null)
                return Entity.Null;
            NativeList<Entity> prefabs = new NativeList<Entity>(m_GhostCollection.Count, Allocator.Temp);
            foreach (var prefab in m_GhostCollection)
            {
                var ghostAuth = prefab.GetComponent<GhostAuthoringComponent>();
                ghostAuth.ForcePrefabConversion = true;
                var prefabEnt = BakeGameObject(prefab, world, m_BlobAssetStore);
                ghostAuth.ForcePrefabConversion = false;
                world.EntityManager.AddComponentData(prefabEnt, default(Prefab));
                prefabs.Add(prefabEnt);
            }

            var collection = world.EntityManager.CreateEntity();
            world.EntityManager.AddComponentData(collection, default(NetCodeTestPrefabCollection));
            var prefabBuffer = world.EntityManager.AddBuffer<NetCodeTestPrefab>(collection);
            foreach (var prefab in prefabs)
                prefabBuffer.Add(new NetCodeTestPrefab {Value = prefab});
            return collection;
        }
#endif
        public void SetDynamicAssemblyList(bool useDynamicAssemblyList)
        {
            GetSingletonRW<RpcCollection>(ServerWorld).ValueRW.DynamicAssemblyList = useDynamicAssemblyList;
            foreach (var clientWorld in ClientWorlds)
                GetSingletonRW<RpcCollection>(clientWorld).ValueRW.DynamicAssemblyList = useDynamicAssemblyList;
        }

        public void TickUntilClientsHaveAllGhosts(int maxTicks = 64)
        {
            World clientWorld = default;
            GhostCount ghostCount = default;
            Assert.IsTrue(ClientWorlds.Length > 0, "Sanity");
            for (int tickIdx = 0; tickIdx < maxTicks; ++tickIdx)
            {
                Tick();
                for (var worldIdx = 0; worldIdx < ClientWorlds.Length; worldIdx++)
                {
                    clientWorld = ClientWorlds[worldIdx];
                    ghostCount = GetSingleton<GhostCount>(clientWorld);
                    var clientHasAll = ghostCount.GhostCountOnServer != 0 && ghostCount.GhostCountInstantiatedOnClient == ghostCount.GhostCountOnServer;
                    ValidateGhostCount(clientWorld, ghostCount);
                    if (!clientHasAll)
                        goto continueContinue;
                }
                return;
                continueContinue:;
            }
            Assert.Fail($"TickUntilClientsHaveAllGhosts failed after {maxTicks} ticks! {clientWorld.Name} has {ghostCount.ToFixedString()}!");
        }

        public static void ValidateGhostCount(World clientWorld, GhostCount ghostCount)
        {
            using var receivedGhostCount = clientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
            Assert.AreEqual(receivedGhostCount.CalculateEntityCount(), ghostCount.GhostCountReceivedOnClient, $"GhostCount.GhostCountReceivedOnClient struct does not match ghost received count on {clientWorld.Name}!");
            using var instantiatedGhostCount = clientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.Exclude<PendingSpawnPlaceholder>());
            var instancedCount = instantiatedGhostCount.CalculateEntityCount();
            //if(instancedCount > 0) UnityEngine.Debug.Log($"{instancedCount} vs {ghostCount} = {clientWorld.EntityManager.GetChunk(instantiatedGhostCount.ToEntityArray(Allocator.Temp)[0]).Archetype}");
            Assert.AreEqual(instancedCount, ghostCount.GhostCountInstantiatedOnClient, $"GhostCount.GhostCountInstantiatedOnClient struct does not match ghost instance count on {clientWorld.Name}!");
        }

        public NetCodeTestLatencyProfile? Profile { get; private set; }
        public void SetTestLatencyProfile(NetCodeTestLatencyProfile latencyProfile)
        {
            Debug.Assert(Profile == null && m_ServerWorld == null && m_ClientWorlds == null && DriverSimulatedDelay == 0 && DriverSimulatedDrop == 0, "Already setup!");
            Profile = latencyProfile;
            DriverSimulatedDelay = latencyProfile switch
            {
                NetCodeTestLatencyProfile.RTT60ms => 30, // 每方向向上取整为 33.34ms，即 2 个 Tick
                NetCodeTestLatencyProfile.RTT16ms_PL5 => 16, // 每方向向上取整为 16.67ms，即 1 个 Tick
                _ => 0,
            };
            DriverSimulatedDrop = latencyProfile switch
            {
                NetCodeTestLatencyProfile.PL33 => 3, // 每第 N 个包丢失一次
                NetCodeTestLatencyProfile.RTT16ms_PL5 => 20, // 每第 N 个包丢失一次
                _ => 0,
            };
        }

        /// <summary>
        /// 尝试向所有可用的数据包转储记录日志
        /// </summary>
        /// <param name="msg">要记录的消息</param>
        public void TryLogPacket(in FixedString512Bytes msg)
        {
#if NETCODE_DEBUG
            TryPacketDump(m_ServerWorld, msg);
            foreach (var clientWorld in m_ClientWorlds)
                TryPacketDump(clientWorld, msg);

            static void TryPacketDump(World world, in FixedString512Bytes msg)
            {
                if (world == null || !world.IsCreated) return;
                using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<EnablePacketLogging>());
                foreach (var logger in query.ToComponentDataArray<EnablePacketLogging>(Allocator.Temp))
                {
                    if(logger.NetDebugPacketCache.IsCreated)
                        logger.NetDebugPacketCache.Log(msg);
                }
            }
#endif
        }

        internal void RemoveWorldFromUpdateList(World world)
        {
            m_WorldStrategy.RemoveWorldFromUpdateList(world);
        }
    }
}
