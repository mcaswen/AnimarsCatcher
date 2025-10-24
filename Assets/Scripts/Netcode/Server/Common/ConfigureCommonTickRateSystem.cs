using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct ConfigureCommonTickRateSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<ClientServerTickRate>())
        {
            state.EntityManager.CreateSingleton(new ClientServerTickRate
            {
                SimulationTickRate        = 60,
                NetworkTickRate           = 60,
                MaxSimulationStepsPerFrame = 4,
                MaxSimulationStepBatchSize = 4,
            });
        }
    }
}
