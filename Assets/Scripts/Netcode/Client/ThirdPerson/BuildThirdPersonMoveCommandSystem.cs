using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using Unity.CharacterController;

// 处理和计算输入数据，然后转换为移动命令
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(GhostInputSystemGroup))]
public partial struct BuildThirdPersonMoveCommandSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<NetworkStreamInGame>();
        state.RequireForUpdate(SystemAPI.QueryBuilder()
            .WithAll<ThirdPersonPlayer, ThirdPersonPlayerInputs>().Build());
    }

    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.TryGetSingletonEntity<NetworkStreamInGame>(out var connection))
            return;

        var networkTime = SystemAPI.GetSingleton<NetworkTime>();
        var commandTarget = SystemAPI.GetComponent<CommandTarget>(connection).targetEntity;
        
        if (commandTarget == Entity.Null) return; 

        var buffer = state.EntityManager.GetBuffer<ThirdPersonMoveCommand>(commandTarget);

        foreach (var (playerInput, player) in SystemAPI.Query<ThirdPersonPlayerInputs, ThirdPersonPlayer>())
        {
            // 获得角色的向上方向
            var characterLocalTransform = SystemAPI.GetComponent<LocalTransform>(player.ControlledCharacter);
            float3 up = MathUtilities.GetUpFromRotation(characterLocalTransform.Rotation);

            // 计算相机朝向
            quaternion cameraRotation = quaternion.identity;
            if (SystemAPI.HasComponent<OrbitCameraComponent>(player.ControlledCamera))
            {
                var orbitCamera = SystemAPI.GetComponent<OrbitCameraComponent>(player.ControlledCamera);

                cameraRotation = OrbitCameraUtilities.CalculateCameraRotation(
                    up, orbitCamera.PlanarForward, orbitCamera.PitchAngle);
            }

            float3 cameraForwardOnPlane = math.normalizesafe(
                MathUtilities.ProjectOnPlane(MathUtilities.GetForwardFromRotation(cameraRotation), up));

            float3 cameraRight = MathUtilities.GetRightFromRotation(cameraRotation);

            // 把输入折算为世界平面向量
            float3 worldMove = playerInput.MoveInput.y * cameraForwardOnPlane + playerInput.MoveInput.x * cameraRight;
            worldMove = MathUtilities.ClampToMaxLength(worldMove, 1f);

            // 打包命令
            ThirdPersonMoveCommand cmd = default;
            cmd.Tick = networkTime.ServerTick;
            cmd.Move = worldMove;
            cmd.Jump = playerInput.JumpPressed.IsSet(networkTime.ServerTick.SerializedData);
            cmd.Look = playerInput.CameraLookInput;
            cmd.Zoom = playerInput.CameraZoomInput;
            cmd.ControlledEntity = player.ControlledCharacter; // 方便调试查看命令对应的实体

            buffer.AddCommandData(cmd);
            
        }
    }
}
