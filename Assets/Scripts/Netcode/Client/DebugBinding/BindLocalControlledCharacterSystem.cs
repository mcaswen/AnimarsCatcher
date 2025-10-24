// using Unity.Entities;
// using Unity.NetCode;
// using UnityEngine;

// [UpdateInGroup(typeof(SimulationSystemGroup))]
// [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
// public partial class BindLocalCharacterAndCameraSystem : SystemBase
// {
//     protected override void OnCreate()
//     {
//         Enabled = false;

//         RequireForUpdate<MainEntityCamera>();              
//         RequireForUpdate<NetworkId>();                   
//     }

//     protected override void OnUpdate()
//     {
//         Enabled = false;

//         var entityManager = EntityManager;

//         var connection = SystemAPI.GetSingletonEntity<NetworkId>();
//         int localId = SystemAPI.GetComponent<NetworkId>(connection).Value;

//         // 找本地拥有角色
//         Entity localOwnedCharacter = Entity.Null;
//         foreach (var (owner, entity) in SystemAPI.Query<RefRO<GhostOwner>>()
//                                             .WithAll<PredictedGhost, Simulate>()
//                                             .WithNone<Prefab, Disabled>()
//                                             .WithEntityAccess())
//         {
//             if (owner.ValueRO.NetworkId == localId)
//             {
//                 localOwnedCharacter = entity;
//                 break;
//             }
//         }

//         if (localOwnedCharacter == Entity.Null) return; // 下一帧再试

//         // 绑定 ControlledCharacter 和本地拥有角色
//         foreach (var player in SystemAPI.Query<RefRW<ThirdPersonPlayer>>().WithAll<PlayerTag>())
//         {
//             player.ValueRW.ControlledCharacter = localOwnedCharacter;
//         }

//         // 切换主相机跟随对象
//         var cam = SystemAPI.GetSingletonEntity<MainEntityCamera>();
//         var ctl = entityManager.GetComponentData<OrbitCameraControl>(cam);
//         ctl.FollowedCharacterEntity = localOwnedCharacter;
//         entityManager.SetComponentData(cam, ctl);

//         Debug.Log($"[BindLocalCharacterAndCamera] bound to {entityManager.GetName(localOwnedCharacter)} (owner={localId})");
        
//         Enabled = false; // 一次绑定即可
//     }
// }