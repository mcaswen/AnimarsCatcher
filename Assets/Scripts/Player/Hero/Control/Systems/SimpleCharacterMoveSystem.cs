using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.NetCode;
using UnityEngine;

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
[UpdateAfter(typeof(PredictedFixedStepSimulationSystemGroup))]
[UpdateBefore(typeof(TransformSystemGroup))]
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
                            RefRO<CharacterBoxInfo>>()
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
        in CharacterBoxInfo boxInfo,
        float deltaTime)
    {
        float3 moveDirection = control.MoveVector;

        if (math.lengthsq(moveDirection) < 1e-6f)
        {
            return;
        }

        // 平面移动
        moveDirection = math.normalizesafe(new float3(moveDirection.x, 0, moveDirection.z));
        float3 delta = moveDirection * config.MoveSpeed * deltaTime;

        float3 startPosition = localTransform.Position;
        float3 endPosition   = startPosition + new float3(delta.x, 0, delta.z);

        quaternion rotation = localTransform.Rotation;

        // 计算 Box 的世界中心
        float3 localCenter = boxInfo.Center;
        float3 worldCenterFloat3 = startPosition + math.mul(rotation, localCenter);
        Vector3 worldCenter = (Vector3)worldCenterFloat3;

        // HalfExtents + 旋转
        Vector3 halfExtents = (Vector3)boxInfo.HalfExtents;
        Quaternion worldRotation =
            new Quaternion(rotation.value.x, rotation.value.y, rotation.value.z, rotation.value.w);

        // 移动向量 & 距离
        Vector3 moveVector = (Vector3)(endPosition - startPosition);
        float distance = moveVector.magnitude;

        if (distance > 1e-5f)
        {
            Vector3 direction = moveVector / distance;

            // 用 BoxCast 检测前方是否有碰撞
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
                // 若检测到碰撞，移动到距离墙面一点点的地方
                float safeDistance = Mathf.Max(hit.distance - 0.01f, 0f);
                Vector3 corrected = worldCenter + direction * safeDistance;

                // 把 Box 中心的位置反推回角色中心
                float3 correctedOffset = corrected - (Vector3)math.mul(rotation, localCenter);

                localTransform.Position = new float3(
                    correctedOffset.x,
                    localTransform.Position.y,
                    correctedOffset.z
                );
            }
            else
            {
                // 未检测到碰撞，直接走到目标位置
                localTransform.Position = endPosition;
            }
        }
        else
        {
            localTransform.Position = endPosition;
        }

        // 旋转朝向移动方向
        quaternion targetRotation = quaternion.LookRotationSafe(moveDirection, math.up());
        float rotationLerp = 1f - math.exp(-config.RotationSharpness * deltaTime);
        localTransform.Rotation = math.slerp(localTransform.Rotation, targetRotation, rotationLerp);
    }
}
