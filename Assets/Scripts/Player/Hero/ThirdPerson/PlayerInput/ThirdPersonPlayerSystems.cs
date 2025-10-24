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
[UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
[UpdateBefore(typeof(FixedStepSimulationSystemGroup))]
public partial class ThirdPersonPlayerInputsSystem : SystemBase
{
    protected override void OnCreate()
    {
        RequireForUpdate<FixedTickSystem.Singleton>();
        RequireForUpdate(SystemAPI.QueryBuilder().WithAll<ThirdPersonPlayer, ThirdPersonPlayerInputs>().Build());
    }

    protected override void OnUpdate()
    {
        foreach (var (playerInputs, player) in SystemAPI.Query<RefRW<ThirdPersonPlayerInputs>, ThirdPersonPlayer>())
        {
            playerInputs.ValueRW.MoveInput = new float2
            {
                x = (Keyboard.current.dKey.isPressed ? 1f : 0f) + (Keyboard.current.aKey.isPressed ? -1f : 0f),
                y = (Keyboard.current.wKey.isPressed ? 1f : 0f) + (Keyboard.current.sKey.isPressed ? -1f : 0f),
            };

            playerInputs.ValueRW.CameraLookInput = Mouse.current.delta.ReadValue();
            playerInputs.ValueRW.CameraZoomInput = -Mouse.current.scroll.ReadValue().y;

            var netTick = SystemAPI.GetSingleton<NetworkTime>().ServerTick.SerializedData;

            if (Keyboard.current.spaceKey.wasPressedThisFrame)
            {
                playerInputs.ValueRW.JumpPressed.Set(netTick);
            }
        }

    }
}

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
        foreach (var (playerInputs, player) in SystemAPI.Query<ThirdPersonPlayerInputs, ThirdPersonPlayer>().WithAll<Simulate>())
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
        }
    }
}
