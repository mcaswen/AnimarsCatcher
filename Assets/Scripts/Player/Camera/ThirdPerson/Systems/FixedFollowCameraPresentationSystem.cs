// using Unity.Entities;
// using Unity.Mathematics;
// using Unity.Transforms;
// using Unity.CharacterController;
// using Unity.NetCode;
// using UnityEngine;

// [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
// [UpdateInGroup(typeof(PresentationSystemGroup))]
// [UpdateAfter(typeof(CompanionGameObjectUpdateTransformSystem))]
// public partial class FixedFollowCameraPresentationSystem : SystemBase
// {
//     private EntityQuery _cameraQuery;

//     protected override void OnCreate()
//     {
//         _cameraQuery = SystemAPI.QueryBuilder()
//             .WithAll<FixedCamera, FixedCameraControl>()
//             .Build();

//         RequireForUpdate(_cameraQuery);
//     }

//     protected override void OnUpdate()
//     {
//         if (MainGameObjectCamera.Instance == null)
//             return;

//         // 确保前面写 LocalToWorld 的 Job 都完成
//         Dependency.Complete();

//         var localToWorldLookup = SystemAPI.GetComponentLookup<LocalToWorld>(true);
//         var cameraTargetLookup = SystemAPI.GetComponentLookup<CameraTarget>(true);

//         using var entities = _cameraQuery.ToEntityArray(Unity.Collections.Allocator.Temp);

//         foreach (var cameraEntity in entities)
//         {
//             var config  = SystemAPI.GetComponent<FixedCamera>(cameraEntity);
//             var control = SystemAPI.GetComponent<FixedCameraControl>(cameraEntity);

//             var target = control.FollowedCharacterEntity;
//             if (target == Entity.Null)
//                 continue;

//             LocalToWorld targetLtw;

//             // === 关键：本地 Predicted 用自己的 LocalToWorld，避免“多插值一层” ===
//             if (SystemAPI.HasComponent<PredictedGhost>(target))
//             {
//                 // 本地玩家：直接跟预测位置（和 simpleControl 时一模一样）
//                 if (!localToWorldLookup.HasComponent(target))
//                     continue;

//                 targetLtw = localToWorldLookup[target];
//             }
//             else
//             {
//                 // 其他情况（远端 / 插值 ghost）：用官方 CameraTarget 的插值工具
//                 if (!OrbitCameraUtilities.TryGetCameraTargetInterpolatedWorldTransform(
//                         target,
//                         ref localToWorldLookup,
//                         ref cameraTargetLookup,
//                         out targetLtw))
//                     continue;
//             }

//             float3 targetPos = targetLtw.Position;
//             float3 up        = math.up();

//             // ===== yaw + pitch 固定角度逻辑 =====

//             float3 planarForward =
//                 math.normalizesafe(MathUtilities.ProjectOnPlane(new float3(0, 0, 1), up));
//             if (math.lengthsq(planarForward) < 1e-6f)
//                 planarForward = math.normalizesafe(
//                     MathUtilities.ProjectOnPlane(new float3(1, 0, 0), up));

//             quaternion baseRot    = quaternion.LookRotationSafe(planarForward, up);
//             quaternion yawRot     = quaternion.AxisAngle(up, math.radians(config.YawDeg));
//             quaternion yawApplied = math.mul(yawRot, baseRot);
//             float3 right          = MathUtilities.GetRightFromRotation(yawApplied);
//             quaternion pitchRot   = quaternion.AxisAngle(right, math.radians(config.PitchDeg));
//             quaternion orientRot  = math.mul(pitchRot, yawApplied);

//             // 用 orientRot 的后向作为机位方向
//             float3 back   = math.mul(orientRot, new float3(0, 0, -1));
//             float3 camPos = targetPos
//                             + back * config.Distance
//                             + new float3(0, config.Height, 0);

//             float3 lookAt  = targetPos + new float3(0, config.LookUpBias, 0);
//             float3 forward = math.normalizesafe(lookAt - camPos);

//             quaternion camRot = quaternion.LookRotationSafe(forward, up);

//             MainGameObjectCamera.Instance.transform.SetPositionAndRotation(camPos, camRot);
//         }
//     }
// }
