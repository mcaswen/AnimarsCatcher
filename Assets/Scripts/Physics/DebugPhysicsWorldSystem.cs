// using Unity.Burst;
// using Unity.Entities;
// using Unity.Physics;

// [BurstCompile]
// [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
// [UpdateInGroup(typeof(SimulationSystemGroup))]
// public partial struct DebugPhysicsWorldSystem : ISystem
// {
//     public void OnUpdate(ref SystemState state)
//     {
//         var physicsWorldSingleton = SystemAPI.GetSingleton<PhysicsWorldSingleton>();
//         ref var world = ref physicsWorldSingleton.PhysicsWorld;

//         UnityEngine.Debug.Log(
//             $"[DebugPhysicsWorld] Bodies={world.NumBodies}, StaticBodies={world.NumStaticBodies}, DynamicBodies={world.NumDynamicBodies}");
//     }
// }