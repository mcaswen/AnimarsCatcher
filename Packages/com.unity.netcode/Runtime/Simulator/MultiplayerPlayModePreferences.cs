#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using Unity.Scenes;
using UnityEditor;
using UnityEngine;

#if UNITY_USE_MULTIPLAYER_ROLES
using Unity.Multiplayer;
#endif

namespace Unity.NetCode
{
    /// <summary>
    /// `MultiplayerPlayModeWindow` 的开发者偏好设置，仅适用于 Editor
    /// </summary>
    public static class MultiplayerPlayModePreferences
    {
        public const bool DefaultSimulatorEnabled = true;
        public const SimulatorView DefaultSimulatorView = SimulatorView.PingView;

        const int k_MaxPacketDelayMs = 2000;
        const int k_MaxPacketJitterMs = 200;
        const int k_DefaultSimulatorMaxPacketCount = 300;

        static string s_PrefsKeyPrefix = $"MultiplayerPlayMode_{Application.productName}_";
        static string s_PlayModeTypeKey = s_PrefsKeyPrefix + "PlayMode_Type";

        static string s_SimulatorEnabledKey = s_PrefsKeyPrefix + "SimulatorEnabled";
        static string s_RequestedSimulatorViewKey = s_PrefsKeyPrefix + "SimulatorView";
        static string s_SimulatorPreset = s_PrefsKeyPrefix + "SimulatorPreset";

        static string s_PacketDelayMsKey = s_PrefsKeyPrefix + "PacketDelayMs";
        static string s_PacketJitterMsKey = s_PrefsKeyPrefix + "PacketJitterMs";
        static string s_PacketDropPercentageKey = s_PrefsKeyPrefix + "PacketDropRate";
        static string s_PacketFuzzPercentageKey = s_PrefsKeyPrefix + "PacketFuzzRate";

        static string s_RequestedNumThinClientsKey = s_PrefsKeyPrefix + "NumThinClients";
        static string s_StaggerThinClientCreationKey = s_PrefsKeyPrefix + "StaggerThinClientCreation";

        static string s_AutoConnectionAddressKey = s_PrefsKeyPrefix + "AutoConnection_Address";
        static string s_AutoConnectionPortKey = s_PrefsKeyPrefix + "AutoConnection_Port";

        static string s_LagSpikeDurationSelectionKey = s_PrefsKeyPrefix + "LagSpikeDurationSelection";

        static string s_ApplyLoggerSettings = s_PrefsKeyPrefix + "NetDebugLogger_ApplyOverload";
        static string s_LoggerLevelType = s_PrefsKeyPrefix + "NetDebugLogger_LogLevelType";
        static string s_TargetShouldDumpPackets = s_PrefsKeyPrefix + "NetDebugLogger_ShouldDumpPackets";
        static string s_ShowAllSimulatorPresets = s_PrefsKeyPrefix + "ShowAllSimulatorPresets";
        static string s_WarnBatchedTicks = s_PrefsKeyPrefix + "NetDebugLogger_WarnBacthedTicks";
        static string s_WarnBatchedTicksRollingWindow = s_PrefsKeyPrefix + "NetDebugLogger_WarnBatchedTicksRollingWindow";
        static string s_WarnAboveAverageTicksPerFrame = s_PrefsKeyPrefix + "NetDebugLogger_WarnAboveAverageTicksPerFrame";

        /// <summary>
        /// 存储用户是否希望使用客户端模拟器 UTP 模块
        /// </summary>
        public static bool SimulatorEnabled
        {
            get => EditorPrefs.GetBool(s_SimulatorEnabledKey, DefaultSimulatorEnabled);
            set => EditorPrefs.SetBool(s_SimulatorEnabledKey, value);
        }

        /// <summary>
        /// 存储 Simulator 在 Editor 中的首选模式
        /// </summary>
        public static SimulatorView RequestedSimulatorView
        {
            get => (SimulatorView) EditorPrefs.GetInt(s_RequestedSimulatorViewKey, (int) DefaultSimulatorView);
            set
            {
#pragma warning disable CS0618
                if (value == SimulatorView.Disabled)
#pragma warning restore CS0618
                {
                    SimulatorEnabled = false;
                    return;
                }
                EditorPrefs.SetInt(s_RequestedSimulatorViewKey, (int) value);
            }
        }

        /// <inheritdoc cref="SimulatorUtility.Parameters"/>
        public static SimulatorUtility.Parameters ClientSimulatorParameters => new SimulatorUtility.Parameters
        {
            Mode = ApplyMode.AllPackets, MaxPacketSize = NetworkParameterConstants.MaxMessageSize, MaxPacketCount = k_DefaultSimulatorMaxPacketCount,
            PacketDelayMs = PacketDelayMs, PacketJitterMs = PacketJitterMs,
            PacketDropPercentage = PacketDropPercentage, FuzzFactor = PacketFuzzPercentage, PacketDuplicationPercentage = 0,
        };

#if UNITY_USE_MULTIPLAYER_ROLES
        private static ClientServerBootstrap.PlayType MultiplayerRoleFlagsToPlayType(MultiplayerRoleFlags roleFlags)
        {
            switch (roleFlags)
            {
                case MultiplayerRoleFlags.Server:
                    return ClientServerBootstrap.PlayType.Server;
                case MultiplayerRoleFlags.Client:
                    return ClientServerBootstrap.PlayType.Client;
                case MultiplayerRoleFlags.ClientAndServer:
                    return ClientServerBootstrap.PlayType.ClientAndServer;
                default:
                    throw new ArgumentOutOfRangeException(nameof(roleFlags), roleFlags, null);
            }
        }

        private static MultiplayerRoleFlags PlayTypeToMultiplayerRoleFlags(ClientServerBootstrap.PlayType playType)
        {
            switch (playType)
            {
                case ClientServerBootstrap.PlayType.Server:
                    return MultiplayerRoleFlags.Server;
                case ClientServerBootstrap.PlayType.Client:
                    return MultiplayerRoleFlags.Client;
                case ClientServerBootstrap.PlayType.ClientAndServer:
                    return MultiplayerRoleFlags.ClientAndServer;
                default:
                    throw new ArgumentOutOfRangeException(nameof(playType), playType, null);
            }
        }
#endif

        /// <summary>
        /// 表示在 Editor 中进入 Play Mode 时由 <see cref="ClientServerBootstrap"/> 创建哪些类型的 World
        /// </summary>
        public static ClientServerBootstrap.PlayType RequestedPlayType
        {
            get
            {
#if UNITY_USE_MULTIPLAYER_ROLES
                if (Unity.Multiplayer.Editor.EditorMultiplayerRolesManager.EnableMultiplayerRoles)
                {
                    return MultiplayerRoleFlagsToPlayType(Unity.Multiplayer.Editor.EditorMultiplayerRolesManager.ActiveMultiplayerRoleMask);
                }
#endif
                return (ClientServerBootstrap.PlayType) EditorPrefs.GetInt(s_PlayModeTypeKey, (int) ClientServerBootstrap.PlayType.ClientAndServer);
            }
            set
            {
#if UNITY_USE_MULTIPLAYER_ROLES
                if (Unity.Multiplayer.Editor.EditorMultiplayerRolesManager.EnableMultiplayerRoles)
                {
                    Unity.Multiplayer.Editor.EditorMultiplayerRolesManager.ActiveMultiplayerRoleMask = PlayTypeToMultiplayerRoleFlags(value);
                    return;
                }
#endif
                EditorPrefs.SetInt(s_PlayModeTypeKey, (int) value);
            }
        }

        private static string s_SimulateDedicatedServer = s_PrefsKeyPrefix + "SimulateDedicatedServer";
        public static bool SimulateDedicatedServer
        {
            get => EditorPrefs.GetBool(s_SimulateDedicatedServer, false);
            set => EditorPrefs.SetBool(s_SimulateDedicatedServer, value);
        }

        /// <inheritdoc cref="SimulatorUtility.Parameters.PacketDelayMs"/>
        public static int PacketDelayMs
        {
            get => math.clamp(EditorPrefs.GetInt(s_PacketDelayMsKey, 0), 0, k_MaxPacketDelayMs);
            set => EditorPrefs.SetInt(s_PacketDelayMsKey, math.clamp(value, 0, k_MaxPacketDelayMs));
        }

        /// <inheritdoc cref="SimulatorUtility.Parameters.PacketJitterMs"/>
        public static int PacketJitterMs
        {
            get => math.clamp(EditorPrefs.GetInt(s_PacketJitterMsKey, 0), 0, k_MaxPacketJitterMs);
            set => EditorPrefs.SetInt(s_PacketJitterMsKey, math.clamp(value, 0, k_MaxPacketJitterMs));
        }

        /// <inheritdoc cref="SimulatorUtility.Parameters.PacketDropPercentage"/>
        public static int PacketDropPercentage
        {
            get => math.clamp(EditorPrefs.GetInt(s_PacketDropPercentageKey, 0), 0, 100);
            set => EditorPrefs.SetInt(s_PacketDropPercentageKey, math.clamp(value, 0, 100));
        }

        /// <inheritdoc cref="SimulatorUtility.Parameters.FuzzFactor"/>
        public static int PacketFuzzPercentage
        {
            get => math.clamp(EditorPrefs.GetInt(s_PacketFuzzPercentageKey, 0), 0, 100);
            set => EditorPrefs.SetInt(s_PacketFuzzPercentageKey, math.clamp(value, 0, 100));
        }

        /// <summary>
        /// 启用相关功能时，表示通过 <see cref="AutomaticThinClientWorldsUtility"/>
        /// 在 Editor 中创建多少个 Thin Client World
        /// </summary>
        public static int RequestedNumThinClients
        {
            get => math.clamp(EditorPrefs.GetInt(s_RequestedNumThinClientsKey, 0), 0, ClientServerBootstrap.k_MaxNumThinClients);
            set => EditorPrefs.SetInt(s_RequestedNumThinClientsKey, math.clamp(value, 0, ClientServerBootstrap.k_MaxNumThinClients));
        }

        /// <summary>
        /// 启用相关功能时，表示通过 <see cref="AutomaticThinClientWorldsUtility"/>
        /// 在 Editor 中每秒生成多少个 Thin Client World
        /// </summary>
        public static float ThinClientCreationFrequency
        {
            get => math.clamp(EditorPrefs.GetFloat(s_StaggerThinClientCreationKey, 2), 0f, 1_000);
            set => EditorPrefs.SetFloat(s_StaggerThinClientCreationKey, value);
        }

        public static string AutoConnectionAddress
        {
            get => EditorPrefs.GetString(s_AutoConnectionAddressKey, "127.0.0.1");
            set => EditorPrefs.SetString(s_AutoConnectionAddressKey, value);
        }

        public static ushort AutoConnectionPort
        {
            get => (ushort) EditorPrefs.GetInt(s_AutoConnectionPortKey, 0);
            set => EditorPrefs.SetInt(s_AutoConnectionPortKey, value);
        }

        /// <summary>
        /// 映射到一个 <see cref="SimulatorPreset"/>
        /// </summary>
        public static string CurrentNetworkSimulatorPreset
        {
            get => EditorPrefs.GetString(s_SimulatorPreset, null);
            set => EditorPrefs.SetString(s_SimulatorPreset, value);
        }

        /// <summary>
        /// 当前为用户定义的自定义 Preset 时返回 true
        /// </summary>
        public static bool IsCurrentNetworkSimulatorPresetCustom => SimulatorPreset.k_CustomProfileKey.Equals(CurrentNetworkSimulatorPreset, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 硬编码 Lag Spike 值列表中保存的索引
        /// </summary>
        public static int LagSpikeSelectionIndex
        {
            get => EditorPrefs.GetInt(s_LagSpikeDurationSelectionKey, 4); // 默认 1 秒
            set => EditorPrefs.SetInt(s_LagSpikeDurationSelectionKey, value);
        }

        /// <summary>
        /// 为 true 时，强制 <see cref="NetDebugSystem"/> 在启动时设置这些值
        /// </summary>
        public static bool ApplyLoggerSettings
        {
            get => EditorPrefs.GetBool(s_ApplyLoggerSettings, false);
            set => EditorPrefs.SetBool(s_ApplyLoggerSettings, value);
        }

        /// <summary>
        /// 为 true 时，强制 <see cref="NetDebugSystem"/> 在预测 Tick 被批处理时显示警告
        /// </summary>
        public static bool WarnBatchedTicks
        {
            get => EditorPrefs.GetBool(s_WarnBatchedTicks, true);
            set
            {
                EditorPrefs.SetBool(s_WarnBatchedTicks, value);

                foreach (var serverWorld in ClientServerBootstrap.ServerWorlds)
                {
                    if (!serverWorld.IsCreated) continue;
                    using var netDebugQuery = serverWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetDebug>());
                    netDebugQuery.GetSingletonRW<NetDebug>().ValueRW.WarnBatchedTicks = value;
                }
            }
        }

        /// <summary>
        /// 指定计算滚动平均值所使用的帧数
        /// </summary>
        public static int WarnBatchedTicksRollingWindow
        {
            get => EditorPrefs.GetInt(s_WarnBatchedTicksRollingWindow, 4);
            set
            {
                EditorPrefs.SetInt(s_WarnBatchedTicksRollingWindow, value);

                foreach (var serverWorld in ClientServerBootstrap.ServerWorlds)
                {
                    using var netDebugQuery = serverWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetDebug>());
                    netDebugQuery.GetSingletonRW<NetDebug>().ValueRW.WarnBatchedTicksRollingWindowSize = value;
                }
            }
        }

        /// <summary>
        /// 平均值高于此比例时显示警告，设为 0 时只要 Tick 被批处理就始终警告
        /// </summary>
        public static float WarnAboveAverageBatchedTicksPerFrame
        {
            get => EditorPrefs.GetFloat(s_WarnAboveAverageTicksPerFrame, 1.2f);
            set
            {
                EditorPrefs.SetFloat(s_WarnAboveAverageTicksPerFrame, value);

                foreach (var serverWorld in ClientServerBootstrap.ServerWorlds)
                {
                    using var netDebugQuery = serverWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetDebug>());
                    netDebugQuery.GetSingletonRW<NetDebug>().ValueRW.WarnAboveAverageBatchedTicksPerFrame = value;
                }
            }
        }



        /// <summary>
        /// 启用 <see cref="ApplyLoggerSettings"/> 时，强制所有 <see cref="NetDebugSystem"/> Logger 使用此日志级别
        /// </summary>
        public static NetDebug.LogLevelType TargetLogLevel
        {
            get => (NetDebug.LogLevelType) EditorPrefs.GetInt(s_LoggerLevelType, (int) NetDebug.LogLevelType.Notify);
            set => EditorPrefs.SetInt(s_LoggerLevelType, (int)value);
        }

        /// <summary>
        /// 启用 <see cref="ApplyLoggerSettings"/> 时，强制所有 <see cref="NetDebugSystem"/> Logger 将 ShouldDumpPackets 设为此值
        /// </summary>
        public static bool TargetShouldDumpPackets
        {
            get
            {
#if NETCODE_NDEBUG
                return false;
#else
                return EditorPrefs.GetBool(s_TargetShouldDumpPackets, false);
#endif
            }
            set
            {
#if !NETCODE_NDEBUG // 防止写入上方强制为 false 的值
                EditorPrefs.SetBool(s_TargetShouldDumpPackets, value);
#endif
            }
        }

        /// <summary>
        /// 为 true 时显示所有 Simulator Preset，而不只显示当前平台专用项
        /// </summary>
        public static bool ShowAllSimulatorPresets
        {
            get => EditorPrefs.GetBool(s_ShowAllSimulatorPresets, false);
            set => EditorPrefs.SetBool(s_ShowAllSimulatorPresets, value);
        }

        /// <summary>
        /// Editor 中输入的地址是有效连接地址时返回 true
        /// </summary>
        public static bool IsEditorInputtedAddressValidForConnect(out NetworkEndpoint ep)
        {
            if (AutoConnectionPort != 0 && NetworkEndpoint.TryParse(AutoConnectionAddress, AutoConnectionPort, out ep, NetworkFamily.Ipv4) && !ep.IsAny)
                return true;

            if (AutoConnectionPort != 0 && NetworkEndpoint.TryParse(AutoConnectionAddress, AutoConnectionPort, out ep, NetworkFamily.Ipv6) && !ep.IsAny)
                return true;

            ep = default;
            return false;
        }

        /// <summary>
        /// 将选定 Preset 应用到静态保存字段，并覆盖用户可能输入的所有自定义值
        /// </summary>
        /// <param name="preset">要应用的 Preset</param>
        public static void ApplySimulatorPresetToPrefs(SimulatorPreset preset)
        {
            if (!preset.IsCustom)
            {
                PacketDelayMs = preset.PacketDelayMs;
                PacketJitterMs = preset.PacketJitterMs;
                PacketDropPercentage = math.clamp(preset.PacketLossPercent, 0, 100);
                PacketFuzzPercentage = math.clamp(preset.PacketFuzzPercent, 0, 100);
            }
        }
    }

    /// <summary>
    /// 供 PlayMode Tools Window 使用的显示模式
    /// </summary>
    public enum SimulatorView
    {
        [Obsolete("Disabled is no longer supported. Use MultiplayerPlayModePreferences.SimulatorEnabled instead. RemovedAfter Entities 1.x")]
        Disabled = 0,
        PingView = 1,
        PerPacketView = 2,
    }
}
#endif
