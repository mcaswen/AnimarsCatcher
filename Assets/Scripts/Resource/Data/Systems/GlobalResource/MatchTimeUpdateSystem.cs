using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Mathematics;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct MatchTimeUpdateSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<GlobalGameResourceTag>();
        state.RequireForUpdate<GlobalGameResourceState>();
        state.RequireForUpdate<NetworkTime>();
    }

    public void OnUpdate(ref SystemState state)
    {
        // 服务器世界从开局到现在，总共跑了多少秒
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
