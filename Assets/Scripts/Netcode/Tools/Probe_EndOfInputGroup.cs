// using Unity.Burst;
// using Unity.Collections;
// using Unity.Entities;
// using Unity.NetCode;
// using Unity.Transforms;
// using UnityEngine;


// [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
// [UpdateInGroup(typeof(GhostInputSystemGroup))]
// [UpdateAfter(typeof(GhostUpdateSystem))]
// public partial struct Probe_EndOfInputGroup : ISystem
// {
//     public void OnUpdate(ref SystemState s)
//     {
//         if (!SystemAPI.TryGetSingletonEntity<NetworkStreamInGame>(out var conn)) return;
//         var e = SystemAPI.GetComponent<CommandTarget>(conn).targetEntity;
//        // UnityEngine.Debug.LogWarning($"[Probe/InputEnd] target={e}");
//     }
// }

// [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
// [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
// public partial struct Probe_StartOfPredictedGroup : ISystem
// {
//     public void OnUpdate(ref SystemState s)
//     {
//         if (!SystemAPI.TryGetSingletonEntity<NetworkStreamInGame>(out var conn)) return;
//         var e = SystemAPI.GetComponent<CommandTarget>(conn).targetEntity;
//         //UnityEngine.Debug.LogWarning($"[Probe/PredBegin] target={e}");
//     }
// }

// // [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
// // [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]

// // [UpdateAfter(typeof(GhostUpdateSystem))]
// // public partial struct Probe_InPredictedGroup : ISystem
// // {
// //     public void OnUpdate(ref SystemState s)
// //     {
// //         if (!SystemAPI.TryGetSingletonEntity<NetworkStreamInGame>(out var conn)) return;
// //         var e = SystemAPI.GetComponent<CommandTarget>(conn).targetEntity;
// //         // UnityEngine.Debug.LogWarning($"[Probe/PreIn] target={e}");
// //     }
// // }