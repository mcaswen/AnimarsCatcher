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


/// <summary>把本地玩家输入转换为受控第三人称相机的控制数据</summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FixedStepSimulationSystemGroup))]
[BurstCompile]
public partial struct ThirdPersonPlayerBuildCameraControlSystem : ISystem
{
    /// <summary>等待本地输入和玩家控制关系可用</summary>
    /// <param name="state">系统状态</param>
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(SystemAPI.QueryBuilder().WithAll<PlayerInput, ThirdPersonPlayerControl>().Build());
    }

    /// <summary>更新环绕或固定相机的跟随目标与本帧输入</summary>
    /// <param name="state">系统状态</param>
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

                var mainEntityCamera = SystemAPI.GetSingletonEntity<MainEntityCamera>();

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
                var mainEntityCamera = SystemAPI.GetSingletonEntity<MainEntityCamera>();
                
                SystemAPI.SetComponent(mainEntityCamera, cameraControl);
                SystemAPI.SetComponent(playerControl.ControlledCamera, cameraControl);
            }
        }
    }
}
