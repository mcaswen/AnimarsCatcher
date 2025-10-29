using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using Unity.CharacterController;
using System.Diagnostics;

// 处理和计算输入数据，然后转换为移动命令（FixedCamera）
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(GhostInputSystemGroup))]
public partial struct BuildTPMoveCommandWithFixedCameraSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<NetworkStreamInGame>();
        state.RequireForUpdate(SystemAPI.QueryBuilder()
            .WithAll<ThirdPersonPlayerControl, PlayerInput, FixedCamera>().Build());
    }

    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.TryGetSingletonEntity<NetworkStreamInGame>(out var connection))
            return;

        var target = SystemAPI.GetComponent<CommandTarget>(connection).targetEntity;
        if (target == Entity.Null) return;

        if (!SystemAPI.HasComponent<PlayerInput>(target) ||
            !SystemAPI.HasComponent<ThirdPersonPlayerControl>(target))
            return;

        var input  = SystemAPI.GetComponent<PlayerInput>(target);
        var player = SystemAPI.GetComponent<ThirdPersonPlayerControl>(target);

        var networkTime   = SystemAPI.GetSingleton<NetworkTime>();
        var buffer = state.EntityManager.GetBuffer<InputCommand>(target);

        // 获得角色的向上方向
        var characterLt = SystemAPI.GetComponent<LocalTransform>(player.ControlledCharacter);
        float3 up = MathUtilities.GetUpFromRotation(characterLt.Rotation);


        // 计算相机朝向
        quaternion cameraRotation = quaternion.identity;

        if (player.ControlledCamera != Entity.Null)
        {
            cameraRotation = SystemAPI.GetComponent<LocalTransform>(player.ControlledCamera).Rotation;
        }

        // 投射到水平面
        float3 camFwd = MathUtilities.GetForwardFromRotation(cameraRotation);
        float3 camRight = MathUtilities.GetRightFromRotation(cameraRotation);

        float3 camFwdOnPlane  = math.normalizesafe(MathUtilities.ProjectOnPlane(camFwd,   up));
        float3 camRightOnPlane= math.normalizesafe(MathUtilities.ProjectOnPlane(camRight, up));

        // 把输入折算为世界平面向量
        float3 worldMove = input.MoveInput.y * camFwdOnPlane
                            + input.MoveInput.x * camRightOnPlane;
        worldMove = MathUtilities.ClampToMaxLength(worldMove, 1f);

        var tick = networkTime.ServerTick;

        // 按位与取按键状态
        var buttons = default(CommandButtons);

        if (input.RightMouseHeld != 0) buttons |= CommandButtons.RMBHold;
        if (input.RightMouseLongPress.IsSet(tick.SerializedData)) buttons |= CommandButtons.RMBLong;
        if (input.JumpPressed.IsSet(tick.SerializedData)) buttons |= CommandButtons.Jump;
        if (input.InteractPressed.IsSet(tick.SerializedData)) buttons |= CommandButtons.Interact;
        if (input.PausePressed.IsSet(tick.SerializedData)) buttons |= CommandButtons.Pause;

        if (input.RightMouseHeld != 0) UnityEngine.Debug.Log("RightMouseHeld");
        if (input.RightMouseLongPress.IsSet(tick.SerializedData)) UnityEngine.Debug.Log("RightMouseLongPressed");
        if (input.JumpPressed.IsSet(tick.SerializedData)) UnityEngine.Debug.Log("JumpPressed");
        if (input.InteractPressed.IsSet(tick.SerializedData)) UnityEngine.Debug.Log("InteractPressed");
        if (input.PausePressed.IsSet(tick.SerializedData)) UnityEngine.Debug.Log("PausePressed");

        // 打包命令
        InputCommand cmd = default;
        cmd.Tick = tick;
        
        cmd.Move = worldMove;
        cmd.Look = float2.zero;   // 固定镜头，不再驱动可变视角
        cmd.Zoom = 0f;            // 固定镜头，不做缩放

        cmd.Buttons = buttons;

        cmd.RMBHoldStartTick = input.RightMouseHoldStartTick;
        cmd.RMBHeldTicks = input.RightMouseHeldTicks;
        
        cmd.MousePosition = input.MousePosition;

        // cmd.ControlledEntity = player.ControlledCharacter;

        buffer.AddCommandData(cmd);
    }
}
