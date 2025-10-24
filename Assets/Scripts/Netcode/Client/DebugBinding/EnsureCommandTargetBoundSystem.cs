// using Unity.Entities;
// using Unity.NetCode;

// [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
// [UpdateInGroup(typeof(SimulationSystemGroup))]
// [UpdateAfter(typeof(GhostUpdateSystem))]
// [UpdateBefore(typeof(PredictedSimulationSystemGroup))]
// public partial struct BindCommandTargetSystem : ISystem
// {
//     public void OnUpdate(ref SystemState state)
//     {
//         if (!SystemAPI.TryGetSingletonEntity<NetworkStreamInGame>(out var conn)) return;

//         var commandTarget = SystemAPI.GetComponentRW<CommandTarget>(conn);
//         var target = commandTarget.ValueRW.targetEntity;
//         var entityManager = state.EntityManager;

//         if (!NeedRebind(target, entityManager))
//         {
//             return;
//         }

//         var localId = SystemAPI.GetSingleton<NetworkId>().Value;

//         foreach (var (owner, CharacterEntity) in SystemAPI
//                      .Query<RefRO<GhostOwner>>()
//                      .WithAll<CharacterTag>() 
//                      .WithEntityAccess())
//         {
//             if (owner.ValueRO.NetworkId != localId)
//             {
//                 UnityEngine.Debug.LogWarning($"[Client][Bind] Found foreign player {CharacterEntity}, continue");
//                 continue;
//             }

//             commandTarget.ValueRW.targetEntity = CharacterEntity;
//             UnityEngine.Debug.LogWarning($"[Client][Bind] CommandTarget bound to {CharacterEntity}");
//             return;
//         }

//         if (target != Entity.Null)
//         {
//             commandTarget.ValueRW.targetEntity = Entity.Null;
//             UnityEngine.Debug.LogWarning("[Client][Bind] 暂时找不到可用玩家，清空 CommandTarget");
//         }
//     }
    
//     bool NeedRebind(Entity entity, EntityManager entityManager) =>
//             entity == Entity.Null
//             || !entityManager.HasComponent<PredictedGhost>(entity)
//             || !entityManager.HasBuffer<ThirdPersonMoveCommand>(entity);

// }
