namespace AnimarsCatcher.Player
{
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Mathematics;
    using Unity.Transforms;
    using UnityEngine;
    using UnityEngine.InputSystem;
    using Unity.CharacterController;
    using Unity.NetCode;


    /// <summary>
    /// 把本地玩家输入转换为受控第三人称相机的控制数据
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(OrbitCameraSimulationSystem))]
    [UpdateBefore(typeof(ClientFixedFollowCameraSystem))]
    [BurstCompile]
    public partial struct ClientThirdPersonPlayerBuildCameraControlSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate(SystemAPI.QueryBuilder().WithAll<PlayerInput, ThirdPersonPlayerControl>().Build());
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (playerInputs, playerControl) in SystemAPI.Query<PlayerInput, ThirdPersonPlayerControl>())
            {
                if (SystemAPI.HasComponent<OrbitCameraControl>(playerControl.ControlledCamera))
                {
                    // 环绕相机需要同步视角和缩放输入
                    OrbitCameraControl cameraControl = SystemAPI.GetComponent<OrbitCameraControl>(playerControl.ControlledCamera);

                    cameraControl.FollowedCharacterEntity = playerControl.ControlledCharacter;

                    var mainEntityCamera = SystemAPI.GetSingletonEntity<MainEntityCameraTag>();

                    cameraControl.LookDegreesDelta = playerInputs.CameraLookInput;
                    cameraControl.ZoomDelta = playerInputs.CameraZoomInput;

                    SystemAPI.SetComponent(mainEntityCamera, cameraControl);
                    SystemAPI.SetComponent(playerControl.ControlledCamera, cameraControl);
                }

                else if (SystemAPI.HasComponent<FixedCamera>(playerControl.ControlledCamera))
                {
                    // 固定相机只需要更新跟随目标
                    FixedCameraControl cameraControl = SystemAPI.GetComponent<FixedCameraControl>(playerControl.ControlledCamera);

                    cameraControl.FollowedCharacterEntity = playerControl.ControlledCharacter;
                    var mainEntityCamera = SystemAPI.GetSingletonEntity<MainEntityCameraTag>();

                    SystemAPI.SetComponent(mainEntityCamera, cameraControl);
                    SystemAPI.SetComponent(playerControl.ControlledCamera, cameraControl);
                }
            }
        }
    }
}
