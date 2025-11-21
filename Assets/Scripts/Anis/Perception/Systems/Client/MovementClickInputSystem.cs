using System.Diagnostics;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct MovementClickInputSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MovementClickRequest>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        RefRW<MovementClickRequest> request = SystemAPI.GetSingletonRW<MovementClickRequest>();
        var networkTime = SystemAPI.GetSingleton<NetworkTime>();

        foreach (var playInput in SystemAPI.Query<RefRO<PlayerInput>>())
        {
            if (!playInput.ValueRO.LeftMousePressed.IsSet(networkTime.ServerTick.SerializedData))
                continue;

            request.ValueRW.Version += 1;
            request.ValueRW.ScreenPosition = playInput.ValueRO.MousePosition;

            break;
        }
    }
}
