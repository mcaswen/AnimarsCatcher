using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.NetCode;

[BurstCompile]
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

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var networkTime = SystemAPI.GetSingleton<NetworkTime>();
        var tick = networkTime.ServerTick;
        float deltaTime = SystemAPI.Time.DeltaTime;

        foreach (var (localTransformRW, configRO, controlRO) in SystemAPI
                     .Query<RefRW<LocalTransform>, RefRO<SimpleCharacter>, RefRO<SimpleCharacterControl>>()
                     .WithAll<PredictedGhost, Simulate, CharacterTag>())
        {
            var localTransform   = localTransformRW.ValueRO;
            var config = configRO.ValueRO;
            var control  = controlRO.ValueRO;

            float3 moveDirction = control.MoveVector;
            
            if (math.lengthsq(moveDirction) < 1e-6f)
            {
                localTransformRW.ValueRW = localTransform;
                continue;
            }

            moveDirction = math.normalizesafe(new float3(moveDirction.x, 0, moveDirction.z));
            float3 delta = moveDirction * config.MoveSpeed * deltaTime;

            // 平面移动：只改 xz
            float3 position = localTransform.Position;
            position.x += delta.x;
            position.z += delta.z;

            // 旋转朝向移动方向
            quaternion targetRot = quaternion.LookRotationSafe(moveDirction, math.up());
            float k = 1f - math.exp(-config.RotationSharpness * deltaTime);
            localTransform.Rotation = math.slerp(localTransform.Rotation, targetRot, k);

            localTransform.Position = position;
            localTransformRW.ValueRW = localTransform;
        }
    }
}
