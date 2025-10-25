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
/// Apply inputs that need to be read at a variable rate
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.LocalSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FixedStepSimulationSystemGroup))]
// [BurstCompile]
public partial class ThirdPersonPlayerVariableStepControlSystem : SystemBase
{
    // [BurstCompile]
    // public void OnCreate(ref SystemState state)
    // {
    //     state.RequireForUpdate(SystemAPI.QueryBuilder().WithAll<ThirdPersonPlayer, ThirdPersonPlayerInputs>().Build());
    // }

    // [BurstCompile]
    protected override void OnUpdate()
    {
        foreach (var (playerInputs, player) in SystemAPI.Query<PlayerInput, ThirdPersonPlayerControl>().WithAll<Simulate>())
        {
            if (SystemAPI.HasComponent<OrbitCameraControl>(player.ControlledCamera))
            {
                OrbitCameraControl cameraControl = SystemAPI.GetComponent<OrbitCameraControl>(player.ControlledCamera);

                cameraControl.FollowedCharacterEntity = player.ControlledCharacter;

                var mainEntityCamera = SystemAPI.GetSingletonEntity<MainEntityCamera>();

                cameraControl.LookDegreesDelta = playerInputs.CameraLookInput;
                cameraControl.ZoomDelta = playerInputs.CameraZoomInput;

                SystemAPI.SetComponent(mainEntityCamera, cameraControl);
                SystemAPI.SetComponent(player.ControlledCamera, cameraControl);
            }

            else if (SystemAPI.HasComponent<FixedCamera>(player.ControlledCamera))
            {
                FixedCameraControl cameraControl = SystemAPI.GetComponent<FixedCameraControl>(player.ControlledCamera);

                cameraControl.FollowedCharacterEntity = player.ControlledCharacter;

                var mainEntityCamera = SystemAPI.GetSingletonEntity<MainEntityCamera>();

                SystemAPI.SetComponent(mainEntityCamera, cameraControl);
                SystemAPI.SetComponent(player.ControlledCamera, cameraControl);

            }
        }
    }
}