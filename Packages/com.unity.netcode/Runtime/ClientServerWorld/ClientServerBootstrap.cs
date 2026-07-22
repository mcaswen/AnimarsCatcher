using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.NetCode
{
    /// <summary>
    /// ClientServerBootstrap 负责在游戏启动时，或编辑器进入 Play Mode 时，于运行时配置并创建服务器和客户端 World
    /// ClientServerBootstrap 旨在作为自定义 Bootstrap 代码的基类，并提供创建客户端和服务器 World 的工具方法
    /// 它还支持使用 <see cref="AutoConnectPort"/> 端口和 <see cref="DefaultConnectAddress"/> 自动连接客户端与服务器
    /// 对于服务器，ClientServerBootstrap 允许通过 <see cref="DefaultListenAddress"/>，
    /// 将服务器 Transport 绑定到指定监听端口和地址，这在云服务商上运行服务器时尤其有用
    /// </summary>
    /// <remarks>
    /// 准备连接服务器或让服务器接受连接后，强烈建议设置 `Application.runInBackground = true;`，也可以通过 Project Settings 全局设置
    /// 否则应用失去焦点时，例如玩家切换到其他窗口，应用会暂停，NetCode 无法推进 Tick，导致多人游戏停滞并很可能断开连接
    /// Dedicated Server Build 通常应始终启用 `Run in Background`
    /// 对此情况可通过 `WarnAboutApplicationRunInBackground` 提供可抑制的错误警告
    /// </remarks>
    [UnityEngine.Scripting.Preserve]
    public class ClientServerBootstrap : ICustomBootstrap
    {
        /// <summary>
        /// 编辑器中可创建的 Thin Client 最大数量
        /// 此限制最初用于避免用户操作导致编辑器长时间卡顿，
        /// 但用户应能测试大量玩家，例如满足 UTP 测试需求，因此实际上已取消该限制
        /// </summary>
        public const int k_MaxNumThinClients = 1000;

        /// <summary>
        /// 服务器 World 的引用，在默认服务器 World 创建期间分配
        /// 如果创建了多个 World，则引用第一个
        /// </summary>
        public static World ServerWorld => ServerWorlds != null && ServerWorlds.Count > 0 && ServerWorlds[0].IsCreated ? ServerWorlds[0] : null;

        /// <summary>
        /// 客户端 World 的引用，在默认客户端 World 创建期间分配
        /// 如果创建了多个 World，则引用第一个
        /// </summary>
        public static World ClientWorld => ClientWorlds != null && ClientWorlds.Count > 0 && ClientWorlds[0].IsCreated ? ClientWorlds[0] : null;

        /// <summary>
        /// 默认创建流程中创建的全部服务器 World 列表
        /// 如果手动创建此类 World，即未通过 Bootstrap API 创建，则需要手动填充此列表
        /// </summary>
        public static List<World> ServerWorlds => ClientServerTracker.ServerWorlds;

        /// <summary>
        /// 默认创建流程中创建的全部客户端 World 列表，不包括 Thin Client World
        /// 如果手动创建此类 World，即未通过 Bootstrap API 创建，则需要手动填充此列表
        /// </summary>
        public static List<World> ClientWorlds => ClientServerTracker.ClientWorlds;

        /// <summary>
        /// 默认创建流程中创建的全部 Thin Client World 列表
        /// 如果手动创建此类 World，即未通过 Bootstrap API 创建，则需要手动填充此列表
        /// </summary>
        public static List<World> ThinClientWorlds => ClientServerTracker.ThinClientWorlds;

        private static int s_NextThinClientId;

        private static OverrideAutomaticNetcodeBootstrap s_OverrideCache;
        private static bool s_OverrideCacheHasResult;

        /// <summary>
        /// 每次创建新实例时初始化 Bootstrap 类并重置静态数据
        /// </summary>

        public ClientServerBootstrap()
        {
            s_NextThinClientId = 1;
            s_OverrideCache = default;
            s_OverrideCacheHasResult = default;
#if UNITY_SERVER && UNITY_CLIENT
            UnityEngine.Debug.LogError("Both UNITY_SERVER and UNITY_CLIENT defines are present. This is not allowed and will lead to undefined behaviour, they are for dedicated server or client only logic so can't work together.");
#endif
        }

        /// <summary>
        /// 创建不包含任何 NetCode 系统的本地 World 的工具方法
        /// </summary>
        /// <param name="defaultWorldName">默认 World 使用的名称</param>
        /// <returns>已添加默认系统并设为主本地 World 运行的 World，参见 <see cref="WorldFlags"/></returns>
        public static World CreateLocalWorld(string defaultWorldName)
        {
            // 必须在生成系统列表前创建默认 World，才能获得有效的 TypeManager 实例
            // 第一次创建任意 World 时会初始化 TypeManager
            var world = new World(defaultWorldName, WorldFlags.Game);
            if (World.DefaultGameObjectInjectionWorld == null)
                World.DefaultGameObjectInjectionWorld = world;

            var systems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default);
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);
            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);
            return world;
        }

        /// <summary>
        /// 实现 ICustomBootstrap 接口，根据 <see cref="RequestedPlayType"/> 创建默认客户端和服务器 World
        /// 在编辑器中，如果 <see cref="RequestedNumThinClients"/> 不为 0，还会创建 Thin Client World
        /// </summary>
        /// <param name="defaultWorldName">默认 World 使用的名称，此处未使用，可以为 null 或空字符串</param>
        /// <inheritdoc cref="ICustomBootstrap.Initialize"/>
        public virtual bool Initialize(string defaultWorldName)
        {
            // 如果用户在活动场景中添加了 OverrideDefaultNetcodeBootstrap MonoBehaviour，
            // 或在整个项目中禁用了 Bootstrap，则在此处遵循该设置
            if (!DetermineIfBootstrappingEnabled())
                return false;

            CreateDefaultClientServerWorlds();
            return true;
        }

        /// <summary>
        /// 返回活动场景中的第一个 <see cref="OverrideAutomaticNetcodeBootstrap"/>，添加到非活动场景的覆盖设置会报告错误
        /// </summary>
        /// <remarks>出于验证需要，此代码包含一次开销较高的 FindObjectsOfType 调用</remarks>
        /// <param name="logNonErrors">如果为 true，则记录更多细节以便调试流程</param>
        /// <returns>活动场景中的第一个覆盖设置</returns>
        public static OverrideAutomaticNetcodeBootstrap DiscoverAutomaticNetcodeBootstrap(bool logNonErrors = false)
        {
            if (s_OverrideCacheHasResult)
                return s_OverrideCache;
            s_OverrideCacheHasResult = true;

            // 请注意，启用 Domain Reload 时 GetActiveScene 会返回无效场景
            var activeScene = SceneManager.GetActiveScene();
            // 此处必须使用 `FindObjectsInactive.Include`，否则结果数量为零
            var sceneConfigurations = UnityEngine.Object.FindObjectsByType<OverrideAutomaticNetcodeBootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (sceneConfigurations.Length <= 0)
            {
                if(logNonErrors)
                    UnityEngine.Debug.Log($"[DiscoverAutomaticNetcodeBootstrap] Did not find any instances of `OverrideAutomaticNetcodeBootstrap`.");
                return s_OverrideCache;
            }
            Array.Sort(sceneConfigurations); // 先按 `name` 再按 `InstanceId` 排序，使结果尽量具有确定性和可靠性
            for (int i = 0; i < sceneConfigurations.Length; i++)
            {
                var config = sceneConfigurations[i];
                // 场景比较在构建版本中不起作用，因为此时 GameObject 尚未附加到场景
                // 2024 年 8 月更新：启用 Domain Reload 时同样如此
                // 因此，仅在条件允许时执行活动场景验证，即 Editor && UnityEditor.EditorSettings.enterPlayModeOptions == None
                // 注意：双击场景可将其设为活动场景
                var activeSceneIsValid = activeScene.IsValid() || SceneManager.loadedSceneCount == 1;
                var isConfigInActiveScene = !activeSceneIsValid || !config.gameObject.scene.IsValid() || config.gameObject.scene == activeScene;
                if (s_OverrideCache)
                {
                    var msg = $"[DiscoverAutomaticNetcodeBootstrap] Cannot select `OverrideAutomaticNetcodeBootstrap` on GameObject '{config.name}' with value `{config.ForceAutomaticBootstrapInScene}` (in scene '{LogScene(config.gameObject.scene, activeScene)}') as we've already selected another ('{s_OverrideCache.name}' with value `{s_OverrideCache.ForceAutomaticBootstrapInScene}` in scene '{LogScene(s_OverrideCache.gameObject.scene, activeScene)}')!";
                    if (config.gameObject.scene == s_OverrideCache.gameObject.scene || isConfigInActiveScene)
                    {
                        msg += " It's erroneous to have multiple in the same scene!";
                        UnityEngine.Debug.LogError(msg, config);
                    }
                    else
                    {
                        if (logNonErrors)
                        {
                            msg += $" AND this config ('{config.name}') is not in the Active scene!";
                            UnityEngine.Debug.Log(msg, config);
                        }
                    }
                    continue;
                }

                if (isConfigInActiveScene)
                {
                    s_OverrideCache = config;
                    if (logNonErrors)
                        UnityEngine.Debug.Log($"[DiscoverAutomaticNetcodeBootstrap] Using discovered `OverrideAutomaticNetcodeBootstrap` on GameObject '{s_OverrideCache.name}' with value `{s_OverrideCache.ForceAutomaticBootstrapInScene}` (in scene '{LogScene(s_OverrideCache.gameObject.scene, activeScene)}') as it's in the active scene ({LogScene(activeScene, activeScene)})!");
                    continue;
                }

                if (logNonErrors)
                    UnityEngine.Debug.Log($"[DiscoverAutomaticNetcodeBootstrap] Ignoring `OverrideAutomaticNetcodeBootstrap` on GameObject '{config.name}' with value `{config.ForceAutomaticBootstrapInScene}` (in scene '{LogScene(config.gameObject.scene, activeScene)}') as this scene is not the Active scene!");
            }
            return s_OverrideCache;

            static string LogScene(Scene scene, Scene active)
            {
                var isValid = scene.IsValid();
                var extraWhenValid = isValid ? $",name:'{scene.name}',path:'{scene.path}'" : null;
                return $"Scene[buildIdx:{scene.buildIndex},handle:{scene.handle},valid:{isValid},loaded:{scene.isLoaded},isSubScene:{scene.isSubScene},isActive:{(active == scene)},rootCount:{scene.rootCount}{extraWhenValid}]";
            }
        }

        /// <summary>
        /// 自动检测活动场景中是否存在 <see cref="OverrideAutomaticNetcodeBootstrap" />
        /// 如果存在，则使用其值覆盖默认值
        /// </summary>
        /// <param name="logNonErrors">如果为 true，则记录更多细节以便调试流程</param>
        /// <returns>是否存在 <see cref="OverrideAutomaticNetcodeBootstrap"/>，否则返回 false</returns>
        public static bool DetermineIfBootstrappingEnabled(bool logNonErrors = false)
        {
            var automaticNetcodeBootstrap = DiscoverAutomaticNetcodeBootstrap(logNonErrors);
            var automaticBootstrapSettingValue = automaticNetcodeBootstrap
                ? automaticNetcodeBootstrap.ForceAutomaticBootstrapInScene
                : (NetCodeConfig.Global ? NetCodeConfig.Global.EnableClientServerBootstrap : NetCodeConfig.AutomaticBootstrapSetting.EnableAutomaticBootstrap);
            return automaticBootstrapSettingValue == NetCodeConfig.AutomaticBootstrapSetting.EnableAutomaticBootstrap;
        }

        /// <summary>
        /// 根据编辑器 PlayMode 工具中的设置，或 Player 中定义的客户端与服务器设置，创建默认客户端和服务器 World 的工具方法
        /// 应在 `Initialize` 的自定义实现中使用
        /// </summary>
        protected virtual void CreateDefaultClientServerWorlds()
        {
#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
            if (NetCodeConfig.Global != null && NetCodeConfig.Global.HostWorldModeSelection == NetCodeConfig.HostWorldMode.SingleWorld && RequestedPlayType == PlayType.ClientAndServer)
            {
                CreateSingleWorldHost("ClientAndServerWorld");
            }
            else
#endif
            {
                if (RequestedPlayType == PlayType.Server || RequestedPlayType == PlayType.ClientAndServer)
                    CreateServerWorld("ServerWorld");
                if (RequestedPlayType == PlayType.Client || RequestedPlayType == PlayType.ClientAndServer)
                    CreateClientWorld("ClientWorld");
            }

#if UNITY_EDITOR
            if (RequestedPlayType == PlayType.Client || RequestedPlayType == PlayType.ClientAndServer)
            {
                AutomaticThinClientWorldsUtility.BootstrapThinClientWorlds();
            }
#endif
        }

        /// <summary>
        /// 创建 Thin Client World 的工具方法
        /// 可在 `Initialize` 的自定义实现中使用，也可以在运行时动态添加新客户端
        /// </summary>
        /// <returns>Thin Client World 实例</returns>
        public static World CreateThinClientWorld()
        {
            var systems = DefaultWorldInitialization.GetAllSystemTypeIndices(WorldSystemFilterFlags.ThinClientSimulation);
            return CreateThinClientWorld(systems);
        }

        /// <param name="systems">要包含的系统列表</param>
        /// <inheritdoc cref="CreateThinClientWorld()"/>
        public static World CreateThinClientWorld(NativeList<SystemTypeIndex> systems)
        {
#if UNITY_SERVER && !UNITY_EDITOR
            Debug.LogWarning("This executable was built using a 'server-only' build target (likely DGS). Thus, may not be able to successfully initialize thin client world.");
#endif
            var world = new World("ThinClientWorld" + s_NextThinClientId++, WorldFlags.GameThinClient);

            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);

            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);
            ThinClientWorlds.Add(world);

            return world;
        }

#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
        /// <inheritdoc cref="CreateSingleWorldHost(string,Unity.Collections.NativeList{Unity.Entities.SystemTypeIndex})"/>
        public static World CreateSingleWorldHost(string name)
#else
        internal static World CreateSingleWorldHost(string name)

#endif
        {
            var systems = DefaultWorldInitialization.GetAllSystemTypeIndices(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation);
            return CreateSingleWorldHost(name, systems);
        }

        /// <summary>
        /// 创建客户端与服务器合并 World，即单 World Host 的工具方法
        /// 可在 `Initialize` 的自定义实现中使用，也可以在运行时动态添加新客户端
        /// </summary>
        /// <returns></returns>
#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
        public static World CreateSingleWorldHost(string name, NativeList<SystemTypeIndex> systems)
#else
        internal static World CreateSingleWorldHost(string name, NativeList<SystemTypeIndex> systems)
#endif
        {
#if (UNITY_CLIENT || UNITY_SERVER) && !UNITY_EDITOR
                throw new NotImplementedException();
#endif
            var world = new World(name, WorldFlags.GameServer | WorldFlags.GameClient);

            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);
            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);

            if (World.DefaultGameObjectInjectionWorld == null)
                World.DefaultGameObjectInjectionWorld = world;

            return world;
        }

        /// <summary>
        /// 创建新客户端 World 的工具方法
        /// 可在 `Initialize` 的自定义实现或运行时使用，以动态添加新客户端，
        /// 也适用于需要通过代码创建客户端的情况，例如允许选择创建游戏或加入游戏的前端
        /// </summary>
        /// <param name="name">客户端 World 名称</param>
        /// <returns>客户端 World 实例</returns>
        public static World CreateClientWorld(string name)
        {
            var systems = DefaultWorldInitialization.GetAllSystemTypeIndices(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation);
            return CreateClientWorld(name, systems);
        }

        /// <param name="name">客户端 World 名称</param>
        /// <param name="systems">要包含的系统列表</param>
        /// <inheritdoc cref="CreateClientWorld(string)"/>
        public static World CreateClientWorld(string name, NativeList<SystemTypeIndex> systems)
        {
#if UNITY_SERVER && !UNITY_EDITOR
            throw new PlatformNotSupportedException("This executable was built using a 'server-only' build target (likely DGS). Thus, cannot create client worlds.");
#else
            var world = new World(name, WorldFlags.GameClient);

            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);
            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);

            if (World.DefaultGameObjectInjectionWorld == null)
                World.DefaultGameObjectInjectionWorld = world;

            ClientWorlds.Add(world);
            return world;
#endif
        }


        /// <summary>
        /// 可选的客户端 Bootstrap 辅助方法，使自定义 Bootstrap 流程可以复用这部分自动连接逻辑
        /// 读取 <see cref="RequestedPlayType"/>，并在有效时检查默认 AutoConnect 参数
        /// </summary>
        /// <param name="autoConnectEp">用于自动连接的有效 Endpoint</param>
        /// <returns>如果为指定 <see cref="RequestedPlayType"/> 配置了自动连接 Endpoint，则返回 true</returns>
        /// <exception cref="ArgumentOutOfRangeException">RequestedPlayType 枚举值未知时抛出</exception>
        public static bool TryFindAutoConnectEndPoint(out NetworkEndpoint autoConnectEp)
        {
            autoConnectEp = default;

            switch (RequestedPlayType)
            {
                case PlayType.Server:
                case PlayType.ClientAndServer:
                {
                    // 允许使用回环地址和 AutoConnectPort
                    if (HasDefaultAddressAndPortSet(out autoConnectEp))
                    {
                        if (!DefaultConnectAddress.IsLoopback)
                        {
                            UnityEngine.Debug.LogWarning($"DefaultConnectAddress is set to `{DefaultConnectAddress.Address}`, but we expected it to be loopback as we're in mode `{RequestedPlayType}`. Using loopback instead!");
                            autoConnectEp = NetworkEndpoint.LoopbackIpv4;
                        }

                        return true;
                    }

                    // 否则不执行任何操作
                    return false;
                }
                case PlayType.Client:
                {
#if UNITY_EDITOR
                    // 在编辑器中，如果编辑器窗口指定的 Endpoint 地址有效，则优先使用该地址
                    if (AutoConnectPort != 0 && MultiplayerPlayModePreferences.IsEditorInputtedAddressValidForConnect(out autoConnectEp))
                        return true;
#endif

                    // 回退到 AutoConnectPort 与 DefaultConnectAddress 的组合
                    if (HasDefaultAddressAndPortSet(out autoConnectEp))
                        return true;

                    // 否则不执行任何操作
                    return false;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(RequestedPlayType), RequestedPlayType, nameof(TryFindAutoConnectEndPoint));
            }
        }

        /// <summary>
        /// 如果用户代码同时指定了 <see cref="AutoConnectPort"/> 和 <see cref="DefaultConnectAddress"/>，则返回 true
        /// </summary>
        /// <param name="autoConnectEp">组合得到的 <see cref="NetworkEndpoint"/></param>
        /// <returns>如果用户代码同时指定了 <see cref="AutoConnectPort"/> 和 <see cref="DefaultConnectAddress"/>，则返回 true</returns>
        public static bool HasDefaultAddressAndPortSet(out NetworkEndpoint autoConnectEp)
        {
            if (AutoConnectPort != 0 && DefaultConnectAddress != NetworkEndpoint.AnyIpv4)
            {
                autoConnectEp = DefaultConnectAddress.WithPort(AutoConnectPort);
                return true;
            }

            autoConnectEp = default;
            return false;
        }

        /// <summary>
        /// 创建新服务器 World 的工具方法
        /// 需要通过代码创建服务器时，可在 `Initialize` 的自定义实现或游戏逻辑中使用，尤其适用于客户端/服务器构建，
        /// 例如允许选择角色或执行其他逻辑的前端
        /// </summary>
        /// <param name="name">服务器 World 名称</param>
        /// <returns>服务器 World 实例</returns>
        public static World CreateServerWorld(string name)
        {
            var systems = DefaultWorldInitialization.GetAllSystemTypeIndices(WorldSystemFilterFlags.ServerSimulation);
            return CreateServerWorld(name, systems);
        }

        /// <param name="systems">要包含的系统列表</param>
        /// <inheritdoc cref="CreateServerWorld(string)"/>
        public static World CreateServerWorld(string name, NativeList<SystemTypeIndex> systems)
        {
#if UNITY_CLIENT && !UNITY_SERVER && !UNITY_EDITOR
            throw new PlatformNotSupportedException("This executable was built using a 'client-only' build target. Thus, cannot create a server world. In your ProjectSettings, change your 'Client Build Target' to `ClientAndServer` to support creating client-hosted servers.");
#else

            var world = new World(name, WorldFlags.GameServer);

            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);
            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);

            if (World.DefaultGameObjectInjectionWorld == null)
                World.DefaultGameObjectInjectionWorld = world;

            ServerWorlds.Add(world);
            return world;
#endif
        }

        /// <summary>
        /// 自动连接使用的默认端口，默认值为 0，表示不自动连接
        /// 如果将其设为有效端口，则在 `DefaultConnectAddress` 有效时，
        /// 调用 `CreateClientWorld`，包括通过 `CreateDefaultWorlds` 和 `Initialize` 调用，都会尝试连接指定地址和端口
        /// 调用 `CreateServerWorld`，包括通过 `CreateDefaultWorlds` 和 `Initialize` 调用，都会监听指定端口和监听地址
        /// </summary>
        public static ushort AutoConnectPort = 0;
        /// <summary>
        /// <para>使用自动连接时的默认连接地址，此时 `AutoConnectPort` 不为 0
        /// 如果此值为 `NetworkEndPoint.AnyIpv4`，即使指定了端口也不会使用自动连接
        /// 这样可以只启用自动监听而不启用自动连接</para>
        /// <para>在编辑器中以 `PlayType.Client` 运行时，`PlayMode Tools` 窗口指定的地址优先级更高
        /// 如果该地址无效或正在 Player 中运行，则改用 `DefaultConnectAddress`</para>
        /// </summary>
        /// <remarks>请注意，如果设置了 `AutoConnectPort`，它会覆盖 `DefaultConnectAddress.Port`</remarks>
        public static NetworkEndpoint DefaultConnectAddress = NetworkEndpoint.LoopbackIpv4;
        /// <summary>
        /// 使用自动连接时的默认监听地址，此时 `AutoConnectPort` 不为 0
        /// </summary>
        public static NetworkEndpoint DefaultListenAddress = NetworkEndpoint.AnyIpv4;
        /// <summary>
        /// <para>表示创建 World 后服务器是否应自动开始监听传入连接</para>
        /// <para>
        /// 如果设置了 <see cref="AutoConnectPort"/>，服务器应使用 <see cref="DefaultConnectAddress"/>
        /// 和 <see cref="AutoConnectPort"/> 开始监听连接
        /// </para>
        /// </summary>
        public static bool WillServerAutoListen => AutoConnectPort != 0;

        /// <summary>
        /// 当前运行模式
        /// </summary>
        /// <seealso cref="ClientServerBootstrap.RequestedPlayType"/>
        public enum PlayType
        {
            /// <summary>
            /// <para>应用可以作为客户端、服务器或同时作为两者运行
            /// 默认会同时创建客户端和服务器 World，使应用可以在托管游戏的同时作为客户端游玩</para>
            /// <para>
            /// 除非通过 PlayMode 工具修改，否则这是编辑器中运行时的默认模式
            /// </para>
            /// </summary>
            ClientAndServer = 0,
            /// <summary>
            /// 应用作为客户端运行，只创建客户端 World，应用应连接到服务器
            /// </summary>
            Client = 1,
            /// <summary>
            /// 应用作为服务器运行，通常只创建服务器 World，应用只能监听传入连接
            /// </summary>
            Server = 2,
        }

        /// <summary>
        /// 当前 Play Mode，用于配置 Driver 和 World
        /// <br/> - 在编辑器中由 PlayMode 工具窗口决定
        /// <br/> - 在构建版本中由平台决定，即由 UNITY_SERVER 和 UNITY_CLIENT 定义决定，
        /// 而这些定义又受 Project Settings 控制
        /// </summary>
        /// <remarks>
        /// 在构建版本中，使用此标志确定构建是否支持作为客户端、服务器或同时作为两者运行
        /// </remarks>
        public static PlayType RequestedPlayType
        {
            get
            {
#if UNITY_EDITOR
                return MultiplayerPlayModePreferences.RequestedPlayType;
#elif UNITY_SERVER
                return PlayType.Server;
#elif UNITY_CLIENT
                return PlayType.Client;
#else
                return PlayType.ClientAndServer;
#endif
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// 要创建的 Thin Client 数量，仅在编辑器中可用
        /// </summary>
        public static int RequestedNumThinClients => MultiplayerPlayModePreferences.RequestedNumThinClients;
#endif
        // 兼容 Burst 的计数器，可在 Job 或 ISystem 中用于检查是否存在客户端或服务器 World
        internal struct ServerClientCount
        {
            public int serverWorlds;
            public int clientWorlds;
        }
        internal static readonly SharedStatic<ServerClientCount> WorldCounts = SharedStatic<ServerClientCount>.GetOrCreate<ClientServerBootstrap>();

        /// <summary>
        /// 检查是否存在带有 <see cref="WorldFlags.GameServer"/> 的 World
        /// </summary>
        /// <value>是否已创建至少一个带有 <see cref="WorldFlags.GameServer"/> 标志的 World</value>
        public static bool HasServerWorld => WorldCounts.Data.serverWorlds > 0;
        /// <summary>
        /// 检查是否存在带有 <see cref="WorldFlags.GameClient"/> 的 World
        /// </summary>
        /// <value>是否已创建至少一个带有 <see cref="WorldFlags.GameClient"/> 标志的 World</value>
        public static bool HasClientWorlds => WorldCounts.Data.clientWorlds > 0;

        static class ClientServerTracker
        {
            internal static List<World> ServerWorlds;
            internal static List<World> ClientWorlds;
            internal static List<World> ThinClientWorlds;
            static ClientServerTracker()
            {
                ServerWorlds = new List<World>();
                ClientWorlds = new List<World>();
                ThinClientWorlds = new List<World>();
            }
        }

        /// <summary>
        /// 辅助方法，返回一个 IEnumerable，依次遍历所有 <see cref="ServerWorld"/>，
        /// 再遍历 <see cref="AllClientWorldsEnumerator"/> 返回的所有 World，
        /// 后者会先遍历所有 <see cref="ClientWorlds"/>，再遍历所有 <see cref="ThinClientWorlds"/>
        /// </summary>
        /// <returns>一个 IEnumerable</returns>
        public static IEnumerable<World> AllNetCodeWorldsEnumerator()
        {
            foreach (var server in ServerWorlds)
                yield return server;
            foreach (var clientOrThinClient in AllClientWorldsEnumerator())
                yield return clientOrThinClient;
        }
        /// <summary>
        /// 辅助方法，返回一个 IEnumerable，先遍历所有 <see cref="ClientWorlds"/>，再遍历所有 <see cref="ThinClientWorlds"/>
        /// </summary>
        /// <returns>一个 IEnumerable</returns>
        public static IEnumerable<World> AllClientWorldsEnumerator()
        {
            foreach (var client in ClientWorlds)
                yield return client;
            foreach (var thin in ThinClientWorlds)
                yield return thin;
        }

        /// <summary>
        /// 如果对应值为 null 或当前 World 尚未创建，则按条件将指定 World 分配给
        /// DefaultGameObjectInjectionWorld 和/或 CurrentlyActiveGameObjectWorld
        /// </summary>
        /// <param name="world"></param>
        internal static void AssignCurrentActiveWorldIfNotSet(World world)
        {
            if (World.DefaultGameObjectInjectionWorld == null || !World.DefaultGameObjectInjectionWorld.IsCreated)
                World.DefaultGameObjectInjectionWorld = world;
            /*if (ActiveGameObjectWorld.World == null || !ActiveGameObjectWorld.World.IsCreated)
                ActiveGameObjectWorld.World = world;*/
        }
    }

    /// <summary>
    /// NetCode 专用的 World 扩展方法
    /// </summary>
    public static class ClientServerWorldExtensions
    {
        /// <summary>
        /// 检查 World 是否为 Thin Client
        /// </summary>
        /// <param name="world"><see cref="World"/> 实例</param>
        /// <returns><paramref name="world"/> 是否为 Thin Client World</returns>
        public static bool IsThinClient(this World world)
        {
            return (world.Flags&WorldFlags.GameThinClient) == WorldFlags.GameThinClient;
        }
        /// <summary>
        /// 检查非托管 World 是否为 Thin Client
        /// </summary>
        /// <param name="world"><see cref="WorldUnmanaged"/> 实例</param>
        /// <returns><paramref name="world"/> 是否为 Thin Client World</returns>
        public static bool IsThinClient(this WorldUnmanaged world)
        {
            return (world.Flags&WorldFlags.GameThinClient) == WorldFlags.GameThinClient;
        }
        /// <summary>
        /// 检查 World 是否为客户端，Thin Client 也会返回 true
        /// </summary>
        /// <param name="world"><see cref="World"/> 实例</param>
        /// <returns><paramref name="world"/> 是否为客户端或 Thin Client World</returns>
        public static bool IsClient(this World world)
        {
            return ((world.Flags&WorldFlags.GameClient) == WorldFlags.GameClient) || world.IsThinClient();
        }
        /// <summary>
        /// 检查非托管 World 是否为客户端，Thin Client 也会返回 true
        /// </summary>
        /// <param name="world"><see cref="WorldUnmanaged"/> 实例</param>
        /// <returns><paramref name="world"/> 是否为客户端或 Thin Client World</returns>
        public static bool IsClient(this WorldUnmanaged world)
        {
            return ((world.Flags&WorldFlags.GameClient) == WorldFlags.GameClient) || world.IsThinClient();
        }
        /// <summary>
        /// 检查 World 是否为服务器
        /// </summary>
        /// <param name="world"><see cref="World"/> 实例</param>
        /// <returns><paramref name="world"/> 是否为服务器 World</returns>
        public static bool IsServer(this World world)
        {
            return (world.Flags&WorldFlags.GameServer) == WorldFlags.GameServer;
        }
        /// <summary>
        /// 检查非托管 World 是否为服务器
        /// </summary>
        /// <param name="world"><see cref="WorldUnmanaged"/> 实例</param>
        /// <returns><paramref name="world"/> 是否为服务器 World</returns>
        public static bool IsServer(this WorldUnmanaged world)
        {
            return (world.Flags&WorldFlags.GameServer) == WorldFlags.GameServer;
        }

        /// <summary>
        /// 检查 World 是否为单 World Host，即同时承担客户端和服务器角色
        /// </summary>
        /// <param name="world"><see cref="World"/> 实例</param>
        /// <returns><paramref name="world"/> 是否为客户端与服务器合并 World</returns>
        public static bool IsHost(this World world)
        {
            return IsClient(world) && IsServer(world);
        }

        /// <inheritdoc cref="IsHost(World)"/>
        public static bool IsHost(this WorldUnmanaged world)
        {
            return IsClient(world) && IsServer(world);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [CreateAfter(typeof(NetworkStreamReceiveSystem))]
    internal partial struct ConfigureServerWorldSystem : ISystem
    {
        EntityQuery m_SendDataQuery;
        EntityQuery m_TickRateQuery;
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            if (!state.World.IsServer())
                throw new InvalidOperationException("Server worlds must be created with the WorldFlags.GameServer flag");
            var simulationGroup = state.World.GetExistingSystemManaged<SimulationSystemGroup>();
            simulationGroup.SetRateManagerCreateAllocator(new NetcodeServerRateManager(simulationGroup));

            var predictionGroup = state.World.GetExistingSystemManaged<PredictedSimulationSystemGroup>();
            predictionGroup.RateManager = new NetcodeServerPredictionRateManager(predictionGroup);
            ++ClientServerBootstrap.WorldCounts.Data.serverWorlds;
            if (ClientServerBootstrap.WillServerAutoListen)
            {
                SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.Listen(ClientServerBootstrap.DefaultListenAddress.WithPort(ClientServerBootstrap.AutoConnectPort));
            }

            m_SendDataQuery = state.GetEntityQuery(typeof(GhostSendSystemData));
            m_TickRateQuery = state.GetEntityQuery(typeof(ClientServerTickRate));
            ApplyGlobalNetCodeConfigIfPresent(state.World, m_TickRateQuery, m_SendDataQuery);

        }

#if UNITY_EDITOR
        public void OnUpdate(ref SystemState state)
        {
            ApplyGlobalNetCodeConfigIfPresent(state.World, m_TickRateQuery, m_SendDataQuery);
        }
#endif

        internal static void ApplyGlobalNetCodeConfigIfPresent(World world, EntityQuery tickRateQuery, EntityQuery ghostSendQuery)
        {
            var serverConfig = NetCodeConfig.Global;
            if (serverConfig)
            {
                if (tickRateQuery.TryGetSingletonRW<ClientServerTickRate>(out var clientServerTickRate))
                    clientServerTickRate.ValueRW = serverConfig.ClientServerTickRate;
                else
                    world.EntityManager.CreateSingleton(serverConfig.ClientServerTickRate);
                ghostSendQuery.GetSingletonRW<GhostSendSystemData>().ValueRW = NetCodeConfig.Global.GhostSendSystemData;
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
                return;

            --ClientServerBootstrap.WorldCounts.Data.serverWorlds;
            ClientServerBootstrap.ServerWorlds.Remove(state.World);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [CreateAfter(typeof(NetworkStreamReceiveSystem))]
    internal partial struct ConfigureClientWorldSystem : ISystem
    {
        EntityQuery m_TickRateQuery;
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            if (!state.World.IsClient() && !state.World.IsThinClient())
                throw new InvalidOperationException("Client worlds must be created with the WorldFlags.GameClient flag");
            var simulationGroup = state.World.GetExistingSystemManaged<SimulationSystemGroup>();
            simulationGroup.RateManager = new NetcodeClientRateManager(simulationGroup);

            var predictionGroup = state.World.GetExistingSystemManaged<PredictedSimulationSystemGroup>();
            predictionGroup.SetRateManagerCreateAllocator(new NetcodeClientPredictionRateManager(predictionGroup));

            ++ClientServerBootstrap.WorldCounts.Data.clientWorlds;
            if (ClientServerBootstrap.TryFindAutoConnectEndPoint(out var autoConnectEp))
            {
                SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(state.EntityManager, autoConnectEp);
            }
            m_TickRateQuery = state.GetEntityQuery(typeof(ClientTickRate));
            ApplyGlobalNetCodeConfigIfPresent(state.World, m_TickRateQuery);
        }

#if UNITY_EDITOR
        public void OnUpdate(ref SystemState state)
        {
            ApplyGlobalNetCodeConfigIfPresent(state.World, m_TickRateQuery);
        }
#endif

        internal static void ApplyGlobalNetCodeConfigIfPresent(World world, EntityQuery tickRateQuery)
        {
            var clientConfig = NetCodeConfig.Global;
            if (clientConfig)
            {
                if (tickRateQuery.TryGetSingletonRW<ClientTickRate>(out var clientTickRate))
                    clientTickRate.ValueRW = clientConfig.ClientTickRate;
                else
                    world.EntityManager.CreateSingleton(clientConfig.ClientTickRate);
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
                return;

            --ClientServerBootstrap.WorldCounts.Data.clientWorlds;
            ClientServerBootstrap.ClientWorlds.Remove(state.World);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ThinClientSimulation)]
    [CreateAfter(typeof(NetworkStreamReceiveSystem))]
    internal partial struct ConfigureThinClientWorldSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            if (!state.World.IsThinClient())
                throw new InvalidOperationException("ThinClient worlds must be created with the WorldFlags.GameThinClient flag");
            var simulationGroup = state.World.GetExistingSystemManaged<SimulationSystemGroup>();
            simulationGroup.RateManager = new NetcodeClientRateManager(simulationGroup);

            ++ClientServerBootstrap.WorldCounts.Data.clientWorlds;
            if (ClientServerBootstrap.TryFindAutoConnectEndPoint(out var autoConnectEp))
            {
                SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(state.EntityManager, autoConnectEp);
            }
            // Thin Client 没有配置自动连接 Endpoint
            // 检查客户端是否已经手动连接到某个目标，如果是，则连接到同一地址
            else if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
            {
                using var driver = ClientServerBootstrap.ClientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamDriver>());
                UnityEngine.Assertions.Assert.IsFalse(driver.IsEmpty);
                var driverData = driver.ToComponentDataArray<NetworkStreamDriver>(Allocator.Temp);
                UnityEngine.Assertions.Assert.IsTrue(driverData.Length == 1);
                if (driverData[0].LastEndPoint.IsValid)
                    SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(state.EntityManager, driverData[0].LastEndPoint);
            }

            state.Enabled = false;
        }

        public void OnDestroy(ref SystemState state)
        {
            --ClientServerBootstrap.WorldCounts.Data.clientWorlds;
            ClientServerBootstrap.ThinClientWorlds.Remove(state.World);
            AutomaticThinClientWorldsUtility.AutomaticallyManagedWorlds.Remove(state.World);
        }
    }


    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [CreateAfter(typeof(NetworkStreamReceiveSystem))]
    internal partial struct ConfigureSingleWorldHostSystem : ISystem
    {
        EntityQuery m_SendDataQuery;
        EntityQuery m_ClientTickRateQuery;
        EntityQuery m_ClientServerTickRateQuery;
        public void OnCreate(ref SystemState state)
        {
            if (!state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            var simulationGroup = state.World.GetExistingSystemManaged<SimulationSystemGroup>();
            var simulationRateManager = new NetcodeHostRateManager(simulationGroup);
            simulationGroup.SetRateManagerCreateAllocator(simulationRateManager);

            var predictionGroup = state.World.GetExistingSystemManaged<PredictedSimulationSystemGroup>();
            // 在 Host 上只希望预测循环采用固定频率，SimulationSystemGroup 的其余部分仍保持正常帧率
            // 输入收集发生在预测循环之外，以确保不会遗漏输入
            predictionGroup.RateManager = new NetcodeHostPredictionRateManager(predictionGroup, simulationRateManager.TimeTracker);

            ++ClientServerBootstrap.WorldCounts.Data.serverWorlds;
            ++ClientServerBootstrap.WorldCounts.Data.clientWorlds;
            ClientServerBootstrap.ServerWorlds.Add(state.World);
            ClientServerBootstrap.ClientWorlds.Add(state.World);

            state.Enabled = false;

            if (ClientServerBootstrap.WillServerAutoListen)
            {
                SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.Listen(ClientServerBootstrap.DefaultListenAddress.WithPort(ClientServerBootstrap.AutoConnectPort));
            }
            m_SendDataQuery = state.GetEntityQuery(typeof(GhostSendSystemData));
            m_ClientTickRateQuery = state.GetEntityQuery(typeof(ClientTickRate));
            m_ClientServerTickRateQuery = state.GetEntityQuery(typeof(ClientServerTickRate));
            ConfigureServerWorldSystem.ApplyGlobalNetCodeConfigIfPresent(state.World, m_ClientServerTickRateQuery, m_SendDataQuery);
            ConfigureClientWorldSystem.ApplyGlobalNetCodeConfigIfPresent(state.World, m_ClientTickRateQuery);
        }

#if UNITY_EDITOR
        public void OnUpdate(ref SystemState state)
        {
            ConfigureServerWorldSystem.ApplyGlobalNetCodeConfigIfPresent(state.World, m_ClientServerTickRateQuery, m_SendDataQuery);
            ConfigureClientWorldSystem.ApplyGlobalNetCodeConfigIfPresent(state.World, m_ClientTickRateQuery);
        }
#endif
        public void OnDestroy(ref SystemState state)
        {
            if (!state.WorldUnmanaged.IsHost())
                return;

            --ClientServerBootstrap.WorldCounts.Data.serverWorlds;
            --ClientServerBootstrap.WorldCounts.Data.clientWorlds;
            ClientServerBootstrap.ServerWorlds.Remove(state.World);
            ClientServerBootstrap.ClientWorlds.Remove(state.World);
        }
    }
}
