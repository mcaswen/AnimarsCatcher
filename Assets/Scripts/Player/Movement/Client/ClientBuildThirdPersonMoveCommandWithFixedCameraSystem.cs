namespace AnimarsCatcher.Player
{
    using Unity.Burst;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.NetCode;
    using Unity.Transforms;
    using Unity.CharacterController;
    using System.Diagnostics;

    /// <summary>
    /// 在客户端把固定相机坐标系下的输入打包为网络移动命令
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    public partial struct ClientBuildThirdPersonMoveCommandWithFixedCameraSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonEntity<NetworkStreamInGame>(out var connection))
                return;

            var target = SystemAPI.GetComponent<CommandTarget>(connection).targetEntity;
            if (target == Entity.Null)
            {
                UnityEngine.Debug.Log("[CommandBinder] No Command Target Entity found, but it's okay because it will be set up later.");
                return;
            }

            if (!SystemAPI.HasComponent<GhostOwnerIsLocal>(target))
            {
                return;
            }

            if (!SystemAPI.TryGetSingleton<PlayerInput>(out var input) ||
                !SystemAPI.TryGetSingleton<ThirdPersonPlayerControl>(out var playerControl))
            {
                UnityEngine.Debug.LogWarning("[CommandBinder] No PlayerInput or ThirdPersonPlayerControl singleton found");
                return;
            }

            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var inputCommandBuffer = state.EntityManager.GetBuffer<InputCommand>(target);

            // 固定相机玩法使用世界上方向作为移动平面法线
            float3 up = math.up();

            // 相机缺失时使用单位旋转，保证命令仍可安全构建
            quaternion cameraRotation = quaternion.identity;

            if (playerControl.ControlledCamera != Entity.Null)
            {
                cameraRotation = SystemAPI.GetComponent<LocalTransform>(playerControl.ControlledCamera).Rotation;
            }

            // 投影相机基向量，避免俯仰角向移动命令引入垂直分量
            float3 cameraForward = MathUtilities.GetForwardFromRotation(cameraRotation);
            float3 cameraRight = MathUtilities.GetRightFromRotation(cameraRotation);

            float3 cameraForwardOnPlane  = math.normalizesafe(MathUtilities.ProjectOnPlane(cameraForward, up));
            float3 cameraRightOnPlane = math.normalizesafe(MathUtilities.ProjectOnPlane(cameraRight, up));

            // 将二维输入转换为服务器和客户端一致的世界平面向量
            float3 worldMove = input.MoveInput.y * cameraForwardOnPlane + input.MoveInput.x * cameraRightOnPlane;
            worldMove = MathUtilities.ClampToMaxLength(worldMove, 1f);

            var tick = networkTime.ServerTick;

            // 离散输入合并为位标记，随同一 Tick 的移动命令发送
            var buttons = default(CommandButtons);

            if (input.RightMouseHeld != 0) buttons |= CommandButtons.RMBHold;
            if (input.RightMouseLongPress.IsSet(tick.SerializedData)) buttons |= CommandButtons.RMBLong;
            if (input.JumpPressed.IsSet(tick.SerializedData)) buttons |= CommandButtons.Jump;
            if (input.InteractPressed.IsSet(tick.SerializedData)) buttons |= CommandButtons.Interact;
            if (input.PausePressed.IsSet(tick.SerializedData)) buttons |= CommandButtons.Pause;

            // 固定相机不发送视角和缩放增量
            InputCommand command = default;
            command.Tick = tick;

            command.Move = worldMove;
            command.Look = float2.zero;
            command.Zoom = 0f;

            command.Buttons = buttons;

            inputCommandBuffer.AddCommandData(command);
        }
    }
}
