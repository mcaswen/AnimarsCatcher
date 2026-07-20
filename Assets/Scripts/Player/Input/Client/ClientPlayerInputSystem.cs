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
    /// 在客户端输入组中采集键鼠状态并写入玩家输入组件
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [UpdateBefore(typeof(FixedStepSimulationSystemGroup))]
    public partial class ClientPlayerInputSystem : SystemBase
    {
        private const float RightMouseLongPressThreshold = 0.35f;

        protected override void OnCreate()
        {
            RequireForUpdate<FixedTickState>();
            RequireForUpdate(SystemAPI.QueryBuilder().WithAll<ThirdPersonPlayerControl, PlayerInput>().Build());
        }

        protected override void OnUpdate()
        {
            // UI 输入锁采用引用计数，任一面板占用时都不能向玩法层传递输入
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

                    // 锁定期间清除脉冲，避免解锁后补触发旧操作
                    input.ValueRW.InteractPressed  = default;
                    input.ValueRW.PausePressed     = default;

                }

                return;
            }

            // 原始设备状态只采集一次，所有本地输入实体共享同一帧快照
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;

            float deltaTime  = SystemAPI.Time.DeltaTime;
            // 离散脉冲必须绑定服务器 Tick 才能参与 NetCode 预测和回滚
            uint tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick.SerializedData;
            var context   = new InputContext(deltaTime, tick, RightMouseLongPressThreshold);

            var rawInputState = new KeyboardMouseState
            {
                Move = new float2(
                    (keyboard.dKey.isPressed ? 1f : 0f) + (keyboard.aKey.isPressed ? -1f : 0f),
                    (keyboard.wKey.isPressed ? 1f : 0f) + (keyboard.sKey.isPressed ? -1f : 0f)),

                LookDelta = mouse != null ? mouse.delta.ReadValue() : float2.zero,

                Scroll = mouse != null ? -mouse.scroll.ReadValue().y : 0f,

                SpaceDown = keyboard.spaceKey.wasPressedThisFrame,
                EKeyDown = keyboard.eKey.wasPressedThisFrame,

                RightHeld = mouse != null && mouse.rightButton.isPressed,
                LeftMousePressed = mouse != null && mouse.leftButton.wasPressedThisFrame,

                MousePosition = mouse != null ? mouse.position.ReadValue() : default
            };

            foreach (var inputRW in SystemAPI.Query<RefRW<PlayerInput>>())
            {
                PlayerInputFeature.ApplyKeyboardInput(ref inputRW.ValueRW, in rawInputState, in context);
                PlayerInputFeature.ApplyMouseInputs(ref inputRW.ValueRW, in rawInputState, in context);
            }
        }
    }
}
