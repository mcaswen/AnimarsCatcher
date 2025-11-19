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
        state.RequireForUpdate<NetworkTime>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var time = SystemAPI.GetSingleton<NetworkTime>();

        float deltaTime = SystemAPI.Time.DeltaTime;

        var resourceState = SystemAPI.GetSingletonRW<GlobalGameResourceState>();

        int previous = resourceState.ValueRO.MatchTimeSeconds;
        int next = previous + (int)math.floor(deltaTime);

        if (next != previous)
        {
            resourceState.ValueRW.MatchTimeSeconds = next;
            UnityEngine.Debug.Log($"[MatchTimeUpdateSystem] Match time updated to {next} seconds.");
        }
    }
}
