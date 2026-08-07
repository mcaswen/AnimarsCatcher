namespace AnimarsCatcher.Player
{
    using AnimarsCatcher.Core;
    using Unity.Burst;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.Transforms;
    using Unity.NetCode;
    using UnityEngine;

    /// <summary>
    /// 在客户端预测与服务器权威世界中移动简化角色
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    public partial struct SimpleCharacterMoveSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;

            foreach (var (localTransformRW,
                          configRO,
                          controlRO,
                          boxInfoRO) in SystemAPI
                         .Query<RefRW<LocalTransform>,
                                RefRO<SimpleCharacter>,
                                RefRO<SimpleCharacterControl>,
                                RefRO<CharacterBoxGeometry>>()
                         .WithAll<PredictedGhost, Simulate, CharacterTag>())
            {
                MoveWithBoxCast(
                    ref localTransformRW.ValueRW,
                    in configRO.ValueRO,
                    in controlRO.ValueRO,
                    in boxInfoRO.ValueRO,
                    deltaTime
                );
            }
        }

        private void MoveWithBoxCast(
            ref LocalTransform localTransform,
            in SimpleCharacter config,
            in SimpleCharacterControl control,
            in CharacterBoxGeometry boxInfo,
            float deltaTime)
        {
            float3 moveDirection = control.MoveVector;

            if (math.lengthsq(moveDirection) < 1e-6f)
            {
                return;
            }

            // 简化角色只允许在水平面移动
            moveDirection = PlanarMath.NormalizeXZOrDefault(moveDirection, float3.zero);
            float3 delta = moveDirection * config.MoveSpeed * deltaTime;

            float3 startPosition = localTransform.Position;
            float3 endPosition   = startPosition + new float3(delta.x, 0, delta.z);

            quaternion rotation = localTransform.Rotation;

            // BoxCast 从碰撞盒世界中心发起，不能直接使用角色原点
            float3 localCenter = boxInfo.Center;
            float3 worldCenterFloat3 = startPosition + math.mul(rotation, localCenter);
            Vector3 worldCenter = (Vector3)worldCenterFloat3;

            // 使用 Authoring 烘焙的半尺寸和角色当前旋转构建检测盒
            Vector3 halfExtents = (Vector3)boxInfo.HalfExtents;
            Quaternion worldRotation =
                new Quaternion(rotation.value.x, rotation.value.y, rotation.value.z, rotation.value.w);

            // Physics.BoxCast 需要归一化方向和独立距离
            Vector3 moveVector = (Vector3)(endPosition - startPosition);
            float distance = moveVector.magnitude;

            if (distance > 1e-5f)
            {
                Vector3 direction = moveVector / distance;

                // 在完整位移路径上检测碰撞，避免高速移动穿过薄墙
                if (Physics.BoxCast(
                        worldCenter,
                        halfExtents,
                        direction,
                        out RaycastHit hit,
                        worldRotation,
                        distance,
                        ~0,
                        QueryTriggerInteraction.Ignore))
                {
                    // 保留少量安全距离，避免下一帧从墙体内部开始检测
                    float safeDistance = Mathf.Max(hit.distance - 0.01f, 0f);
                    Vector3 corrected = worldCenter + direction * safeDistance;

                    // 将修正后的碰撞盒中心反推为角色原点
                    float3 correctedOffset = corrected - (Vector3)math.mul(rotation, localCenter);

                    localTransform.Position = new float3(
                        correctedOffset.x,
                        localTransform.Position.y,
                        correctedOffset.z
                    );
                }
                else
                {
                    // 路径无碰撞时直接采用目标位置
                    localTransform.Position = endPosition;
                }
            }
            else
            {
                localTransform.Position = endPosition;
            }

            // 使用指数平滑转向移动方向，使不同帧率下转向速度一致
            quaternion targetRotation = quaternion.LookRotationSafe(moveDirection, math.up());
            float rotationLerp = 1f - math.exp(-config.RotationSharpness * deltaTime);
            localTransform.Rotation = math.slerp(localTransform.Rotation, targetRotation, rotationLerp);
        }
    }
}
