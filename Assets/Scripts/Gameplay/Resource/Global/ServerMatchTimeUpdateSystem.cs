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
    public partial struct ServerMatchTimeUpdateSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GlobalGameResourceTag>();
            state.RequireForUpdate<GlobalGameResourceState>();
            state.RequireForUpdate<NetworkTime>();
        }

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
