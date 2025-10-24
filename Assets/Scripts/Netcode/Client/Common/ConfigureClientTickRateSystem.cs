using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
[UpdateBefore(typeof(NetworkTimeSystem))]
public partial struct ConfigureClientPredictionSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<ClientTickRate>())
        {
            state.EntityManager.CreateSingleton(new ClientTickRate
            {
                // —— 预测时间缩放（用于 Predicted 世界）
                PredictionTimeScaleMin = 0.90f, // [0.01, 1.0)
                PredictionTimeScaleMax = 1.10f, // (1.0, 2.0]

                // —— 插值时间缩放（用于 Interpolated 世界）
                InterpolationTimeScaleMin = 0.90f, // [0.01, 1.0)
                InterpolationTimeScaleMax = 1.10f, // (1.0, 2.0]

                // 让输入“提前”到达服务器，减少 miss
                TargetCommandSlack = 2,
            });
        }
    }

    public void OnUpdate(ref SystemState state)
    {
        var tickRateRW = SystemAPI.GetSingletonRW<ClientTickRate>();
        var tickRateValueRO  = tickRateRW.ValueRO;

        // —— 防御式钳制，避免其他系统把值写崩
        tickRateValueRO.PredictionTimeScaleMin    = math.clamp(tickRateValueRO.PredictionTimeScaleMin,    0.01f, 0.999f);
        tickRateValueRO.PredictionTimeScaleMax    = math.clamp(tickRateValueRO.PredictionTimeScaleMax,    1.001f, 2.0f);
        tickRateValueRO.InterpolationTimeScaleMin = math.clamp(tickRateValueRO.InterpolationTimeScaleMin, 0.01f, 0.999f);
        tickRateValueRO.InterpolationTimeScaleMax = math.clamp(tickRateValueRO.InterpolationTimeScaleMax, 1.001f, 2.0f);

        if (tickRateValueRO.TargetCommandSlack <= 0) 
            tickRateValueRO.TargetCommandSlack = 2;

        tickRateRW.ValueRW = tickRateValueRO;
    }
}
