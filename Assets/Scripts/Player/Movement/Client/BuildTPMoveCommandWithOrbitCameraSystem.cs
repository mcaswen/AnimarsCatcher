using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using Unity.CharacterController;

// 处理和计算输入数据，然后转换为移动命令（OrbitCamera）
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(GhostInputSystemGroup))]
public partial struct BuildTPMoveCommandWithOrbitCameraSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<NetworkStreamInGame>();
        state.RequireForUpdate(SystemAPI.QueryBuilder()
            .WithAll<ThirdPersonPlayerControl, PlayerInput>().Build());
    }

    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.TryGetSingletonEntity<NetworkStreamInGame>(out var connection))
            return;

        var networkTime = SystemAPI.GetSingleton<NetworkTime>();
        var commandTarget = SystemAPI.GetComponent<CommandTarget>(connection).targetEntity;

        if (commandTarget == Entity.Null) return;

        var buffer = state.EntityManager.GetBuffer<InputCommand>(commandTarget);

        foreach (var (input, player) in SystemAPI.Query<PlayerInput, ThirdPersonPlayerControl>())
        {
            // 获得角色的向上方向
            var characterLocalTransform = SystemAPI.GetComponent<LocalTransform>(player.ControlledCharacter);
            float3 up = MathUtilities.GetUpFromRotation(characterLocalTransform.Rotation);

            // 计算相机朝向
            quaternion cameraRotation = quaternion.identity;
            if (SystemAPI.HasComponent<OrbitCamera>(player.ControlledCamera))
            {
                var orbitCamera = SystemAPI.GetComponent<OrbitCamera>(player.ControlledCamera);

                cameraRotation = OrbitCameraUtilities.CalculateCameraRotation(
                    up, orbitCamera.PlanarForward, orbitCamera.PitchAngle);
            }

            // 投射到水平面
            float3 cameraForwardOnPlane = math.normalizesafe(
                MathUtilities.ProjectOnPlane(MathUtilities.GetForwardFromRotation(cameraRotation), up));

            float3 cameraRight = MathUtilities.GetRightFromRotation(cameraRotation);

            // 把输入折算为世界平面向量
            float3 worldMove = input.MoveInput.y * cameraForwardOnPlane + input.MoveInput.x * cameraRight;
            worldMove = MathUtilities.ClampToMaxLength(worldMove, 1f);

            var tick = networkTime.ServerTick;

            // 按位与取按键状态
            var buttons = default(CommandButtons);

            if (input.RightMouseHeld != 0) buttons |= CommandButtons.RMBHold;
            if (input.RightMouseLongPress.IsSet(tick.SerializedData)) buttons |= CommandButtons.RMBLong;
            if (input.JumpPressed.IsSet(tick.SerializedData)) buttons |= CommandButtons.Jump;
            if (input.InteractPressed.IsSet(tick.SerializedData)) buttons |= CommandButtons.Interact;
            if (input.PausePressed.IsSet(tick.SerializedData)) buttons |= CommandButtons.Pause;

            // 打包命令
            InputCommand command = default;
            command.Tick = tick;

            command.Move = worldMove;
            command.Look = input.CameraLookInput;
            command.Zoom = input.CameraZoomInput;

            command.Buttons = buttons;

            buffer.AddCommandData(command);

        }
    }
}
