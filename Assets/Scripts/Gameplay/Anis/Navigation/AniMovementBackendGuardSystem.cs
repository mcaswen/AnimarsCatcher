using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在模拟开始前验证 Ani 移动后端配置与启用 Tag 严格一致
    /// </summary>
    [WorldSystemFilter(
        WorldSystemFilterFlags.ServerSimulation |
        WorldSystemFilterFlags.ClientSimulation |
        WorldSystemFilterFlags.ThinClientSimulation |
        WorldSystemFilterFlags.LocalSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct AniMovementBackendGuardSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!AniMovementBackendWorldUtility.TryValidateWorld(state.World, out string reason))
            {
                StopWorld(ref state, reason);
            }
        }

        private static void StopWorld(ref SystemState state, string reason)
        {
            Debug.LogError($"[AniMovementBackendGuard] {reason}，已停止 World {state.World.Name} 的后续更新");
            state.World.QuitUpdate = true;
            state.Enabled = false;
        }
    }
}
