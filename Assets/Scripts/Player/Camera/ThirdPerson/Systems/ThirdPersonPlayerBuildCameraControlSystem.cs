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


[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.LocalSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FixedStepSimulationSystemGroup))]
[BurstCompile]
public partial struct ThirdPersonPlayerBuildCameraControlSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(SystemAPI.QueryBuilder().WithAll<PlayerInput, ThirdPersonPlayerControl>().Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var (playerInputs, playerControl) in SystemAPI.Query<PlayerInput, ThirdPersonPlayerControl>().WithAll<Simulate>())
        {
        
            if (SystemAPI.HasComponent<OrbitCameraControl>(playerControl.ControlledCamera))
            {
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
                FixedCameraControl cameraControl = SystemAPI.GetComponent<FixedCameraControl>(playerControl.ControlledCamera);

                cameraControl.FollowedCharacterEntity = playerControl.ControlledCharacter;

                foreach (var fixedCameraControl in SystemAPI.Query<RefRW<FixedCameraControl>>().WithAll<Simulate, GhostOwner, MainEntityCamera>())
                {
                    fixedCameraControl.ValueRW = cameraControl;
                }

                SystemAPI.SetComponent(playerControl.ControlledCamera, cameraControl);
            }
        }
    }
}