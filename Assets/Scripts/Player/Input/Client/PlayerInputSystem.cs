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

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(GhostInputSystemGroup))]
[UpdateBefore(typeof(FixedStepSimulationSystemGroup))]
public partial class PlayerInputSystem : SystemBase
{
    private const float kRightMouseLongPressThreshold = 0.35f;

    protected override void OnCreate()
    {
        RequireForUpdate<FixedTickSystem.Singleton>();
        RequireForUpdate(SystemAPI.QueryBuilder().WithAll<ThirdPersonPlayerControl, PlayerInput>().Build());
    }

    protected override void OnUpdate()
    {
        int lockCount = 0;
        if (SystemAPI.HasSingleton<PlayerInputLockState>())
        {
            lockCount = SystemAPI.GetSingleton<PlayerInputLockState>().LockCount;
        }

        if (lockCount > 0)
        {
            foreach (var input in SystemAPI.Query<RefRW<PlayerInput>>())
            {
                input.ValueRW.MoveInput        = float2.zero;
                input.ValueRW.CameraZoomInput  = 0f;

                // 清理按键信息，避免解锁瞬间触发操作
                input.ValueRW.InteractPressed  = default;
                input.ValueRW.PausePressed     = default;

            }

            return;
        }

        var keyBoard = Keyboard.current;
        var mouse = Mouse.current;

        float deltaTime  = SystemAPI.Time.DeltaTime;
        uint tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick.SerializedData;
        var context   = new InputContext(deltaTime, tick, 0.35f);

        var raw = new KeyBoardMouseState
        {
            Move = new float2(
                (keyBoard.dKey.isPressed ? 1f : 0f) + (keyBoard.aKey.isPressed ? -1f : 0f),
                (keyBoard.wKey.isPressed ? 1f : 0f) + (keyBoard.sKey.isPressed ? -1f : 0f)),

            LookDelta = mouse != null ? mouse.delta.ReadValue() : float2.zero,

            Scroll = mouse != null ? -mouse.scroll.ReadValue().y : 0f,

            SpaceDown = keyBoard.spaceKey.wasPressedThisFrame,
            EDown = keyBoard.eKey.wasPressedThisFrame,

            RightHeld = mouse != null && mouse.rightButton.isPressed,
            LeftMousePressed = mouse != null && mouse.leftButton.wasPressedThisFrame,

            MousePosition = mouse != null ? mouse.position.ReadValue() : default
        };

        foreach (var inputRW in SystemAPI.Query<RefRW<PlayerInput>>())
        {
            PlayerInputFeature.ApplyKeyboardInput(ref inputRW.ValueRW, in raw, in context);
            PlayerInputFeature.ApplyMouseInputs(ref inputRW.ValueRW, in raw, in context);
        }
    }
}

