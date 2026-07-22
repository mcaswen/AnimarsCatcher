#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 把此组件添加到 Singleton Entity，以配置 NetCode 包日志级别并启用或禁用数据包转储
    /// </summary>
    public struct NetCodeDebugConfig : IComponentData
    {
        /// <summary>
        /// NetCode 使用的日志级别，默认值为 <see cref="NetDebug.LogLevelType.Notify"/>
        /// </summary>
        public NetDebug.LogLevelType LogLevel;
        /// <summary>
        /// 启用或禁用数据包转储
        /// 数据包转储主要用于调试，CPU 和内存开销都很高
        /// </summary>
        public bool DumpPackets;
    }

#if NETCODE_DEBUG
    /// <summary>
    /// 把 <see cref="NetCodeDebugConfig"/> 复制到 <see cref="NetDebug"/> Singleton 的系统
    /// <see cref="NetCodeDebugConfig.DumpPackets"/> 设为 true 时，向所有连接添加 <see cref="EnablePacketLogging"/> 组件
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    internal partial struct DebugConnections : ISystem
    {
        EntityQuery m_ConnectionsQueryWithout;
        EntityQuery m_ConnectionsQueryWith;

        public bool EditorApplyLoggerSettings;
        public NetCodeDebugConfig ForceSettings;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_ConnectionsQueryWithout = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<NetworkStreamConnection>().WithNone<EnablePacketLogging>());
            m_ConnectionsQueryWith = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<NetworkStreamConnection>().WithAll<EnablePacketLogging>());
        }

        public void OnUpdate(ref SystemState state)
        {
            var netDbg = SystemAPI.GetSingletonRW<NetDebug>();
            if (!SystemAPI.TryGetSingleton<NetCodeDebugConfig>(out var debugConfig))
            {
                // 没有用户自定义配置，使用 NetDebug 默认值
                debugConfig.LogLevel = NetDebug.DefaultLogLevel;
                debugConfig.DumpPackets = false;
            }

#if UNITY_EDITOR
            if (MultiplayerPlayModePreferences.ApplyLoggerSettings)
            {
                debugConfig.LogLevel = MultiplayerPlayModePreferences.TargetLogLevel;
                debugConfig.DumpPackets = MultiplayerPlayModePreferences.TargetShouldDumpPackets;
            }
#endif

            if (netDbg.ValueRO.LogLevel != debugConfig.LogLevel)
            {
                netDbg.ValueRW.LogLevel = debugConfig.LogLevel;
            }

            if (debugConfig.DumpPackets)
            {
                state.EntityManager.AddComponent<EnablePacketLogging>(m_ConnectionsQueryWithout);
            }
            else
            {
                state.EntityManager.RemoveComponent<EnablePacketLogging>(m_ConnectionsQueryWith);
            }
        }
    }
#endif
}
