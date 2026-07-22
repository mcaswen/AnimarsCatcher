#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif

#if NETCODE_DEBUG
using Unity.Entities;

namespace Unity.NetCode
{
    /// <inheritdoc cref="NetDebug.SuppressApplicationRunInBackgroundWarning"/>>
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct WarnAboutApplicationRunInBackground : ISystem, ISystemStartStop
    {
        /// <summary>
        /// 要求用户已连接后才显示此警告
        /// </summary>
        /// <param name="state"></param>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkId>();
        }

        /// <summary>
        /// 处理警告触发
        /// </summary>
        /// <param name="state"></param>
        public void OnUpdate(ref SystemState state)
        {
            ref var netDebug = ref SystemAPI.GetSingletonRW<NetDebug>().ValueRW;
            if (netDebug.SuppressApplicationRunInBackgroundWarning || netDebug.HasWarnedAboutApplicationRunInBackground)
                return;

            // @FIXME 通过两个 World 支持单机模式时需要抑制此警告
            if (!UnityEngine.Application.runInBackground)
            {
                netDebug.HasWarnedAboutApplicationRunInBackground = true;
                UnityEngine.Debug.LogError($"[{state.WorldUnmanaged.Name}] Netcode detected that you don't have Application.runInBackground enabled during multiplayer gameplay. This will lead to your multiplayer stalling (and disconnecting) if and when the application loses focus (e.g. by the player tabbing out). It is highly recommended to enable \"Run in Background\" via `Application.runInBackground = true;` when connecting, or project-wide via 'Project Settings > Player > Resolution and Presentation > Run in Background'.\nSuppress this advice log via `NetDebug.SuppressApplicationRunInBackgroundWarning`.");
            }
        }

        /// <summary>
        /// 断开连接后重置警告
        /// </summary>
        /// <param name="state"></param>
        public void OnStartRunning(ref SystemState state)
        {
            ref var netDebug = ref SystemAPI.GetSingletonRW<NetDebug>().ValueRW;
            netDebug.HasWarnedAboutApplicationRunInBackground = false;
        }

        /// <summary>
        /// 不执行任何操作
        /// </summary>
        /// <param name="state"></param>
        public void OnStopRunning(ref SystemState state)
        {
        }
    }
}
#endif
