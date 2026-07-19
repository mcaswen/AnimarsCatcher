using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Mathematics;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在服务端按整秒更新并同步比赛已进行时间
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MatchTimeUpdateSystem : ISystem
    {
        /// <summary>
        /// 等待全局资源单例和网络时间可用
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GlobalGameResourceTag>();
            state.RequireForUpdate<GlobalGameResourceState>();
            state.RequireForUpdate<NetworkTime>();
        }

        /// <summary>
        /// 仅在秒数变化时写入 Ghost 状态
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // 使用服务端世界时间作为所有客户端一致的计时来源
            double elapsed = SystemAPI.Time.ElapsedTime;

            var resourceState = SystemAPI.GetSingletonRW<GlobalGameResourceState>();

            int previous = resourceState.ValueRO.MatchTimeSeconds;
            int next = (int)math.floor((float)elapsed);

            if (next != previous)
            {
                resourceState.ValueRW.MatchTimeSeconds = next;
            }
        }
    }
}
