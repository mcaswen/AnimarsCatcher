// using Unity.Burst;
// using Unity.Entities;
// using Unity.Mathematics;
// using Unity.Transforms;
// using Unity.NetCode;
// using UnityEngine;

// [BurstCompile]
// [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
// [UpdateInGroup(typeof(PredictedSimulationSystemGroup))] // 预测组：客户端本地预测 & 服务器权威都执行
// public partial struct PredictedPlayerMoveSystem : ISystem
// {   
//     double _next;
//     public void OnCreate(ref SystemState state)
//     {
//         state.RequireForUpdate<PlayerTag>();
//     }

//     public void OnUpdate(ref SystemState state)
//     {
//         // 正在预测的Tick
//         var predictionTick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
//         int count = 0;

//         foreach (var (lt, speed, inputBuffer) in SystemAPI
//                      .Query<RefRW<LocalTransform>, RefRO<MoveSpeed>, DynamicBuffer<MoveCommand>>()
//                      .WithAll<PlayerTag, PredictedGhost>())  // 服务器上所有都是预测；客户端仅本地拥有者是预测
//         {
//             count++;
//             inputBuffer.GetDataAtTick(predictionTick, out MoveCommand cmd); // 按Tick取命令

//             float3 dir = new float3(cmd.Move.x, 0, cmd.Move.y);
//             var t = lt.ValueRO;
//             t.Position += dir * (speed.ValueRO.Value * SystemAPI.Time.DeltaTime);
//             lt.ValueRW = t;
//         }
// #if !UNITY_EDITOR
//         if (SystemAPI.Time.ElapsedTime > _next)
//         {
//             _next = SystemAPI.Time.ElapsedTime + 1.0;
//             Debug.Log($"[{state.WorldUnmanaged.Name}] PredictedGhost count={count}");
//         }
// #endif
//     }

// }
