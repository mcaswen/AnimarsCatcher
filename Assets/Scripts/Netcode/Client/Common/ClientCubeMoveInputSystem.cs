using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(GhostInputSystemGroup))]

public struct CubeTag : IComponentData {}

public partial struct ClientCubeMoveInputSystem : ISystem
{
    double _next;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<CubeTag>();    
    }

    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.TryGetSingleton<CommandTarget>(out var target) || target.targetEntity == Entity.Null)
            return;

        // 采样本地输入
        float3 move = new float3(Input.GetAxisRaw("Vertical"), Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Jump"));
        if (math.lengthsq(move) > 1f) move = math.normalize(move);

        // 当前服务器Tick
        var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;

        var buffer = state.EntityManager.GetBuffer<ThirdPersonMoveCommand>(target.targetEntity);
        buffer.AddCommandData(new ThirdPersonMoveCommand { Tick = tick, Move = move });
    }
}
