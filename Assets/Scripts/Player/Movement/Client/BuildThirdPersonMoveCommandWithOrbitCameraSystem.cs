namespace AnimarsCatcher.Player
{
    using Unity.Burst;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.NetCode;
    using Unity.Transforms;
    using Unity.CharacterController;

    /// <summary>
    /// 在客户端把环绕相机坐标系下的输入打包为网络移动命令
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    public partial struct BuildThirdPersonMoveCommandWithOrbitCameraSystem : ISystem
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
                // 角色可能位于倾斜表面，移动平面必须使用角色自身上方向
                var characterLocalTransform = SystemAPI.GetComponent<LocalTransform>(player.ControlledCharacter);
                float3 up = MathUtilities.GetUpFromRotation(characterLocalTransform.Rotation);

                // 从环绕相机状态重建与表现一致的相机旋转
                quaternion cameraRotation = quaternion.identity;
                if (SystemAPI.HasComponent<OrbitCamera>(player.ControlledCamera))
                {
                    var orbitCamera = SystemAPI.GetComponent<OrbitCamera>(player.ControlledCamera);

                    cameraRotation = OrbitCameraUtilities.CalculateCameraRotation(
                        up, orbitCamera.PlanarForward, orbitCamera.PitchAngle);
                }

                // 前方向投影到角色移动平面，右方向由相机旋转直接获得
                float3 cameraForwardOnPlane = math.normalizesafe(
                    MathUtilities.ProjectOnPlane(MathUtilities.GetForwardFromRotation(cameraRotation), up));

                float3 cameraRight = MathUtilities.GetRightFromRotation(cameraRotation);

                // 将二维输入转换为服务器和客户端一致的世界方向
                float3 worldMove = input.MoveInput.y * cameraForwardOnPlane + input.MoveInput.x * cameraRight;
                worldMove = MathUtilities.ClampToMaxLength(worldMove, 1f);

                var tick = networkTime.ServerTick;

                // 离散输入合并为位标记，随同一 Tick 的移动命令发送
                var buttons = default(CommandButtons);

                if (input.RightMouseHeld != 0) buttons |= CommandButtons.RMBHold;
                if (input.RightMouseLongPress.IsSet(tick.SerializedData)) buttons |= CommandButtons.RMBLong;
                if (input.JumpPressed.IsSet(tick.SerializedData)) buttons |= CommandButtons.Jump;
                if (input.InteractPressed.IsSet(tick.SerializedData)) buttons |= CommandButtons.Interact;
                if (input.PausePressed.IsSet(tick.SerializedData)) buttons |= CommandButtons.Pause;

                // 环绕相机需要同时发送视角和缩放增量
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
}
