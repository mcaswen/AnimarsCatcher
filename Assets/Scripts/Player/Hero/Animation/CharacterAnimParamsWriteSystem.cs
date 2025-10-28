using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.CharacterController;
using Unity.Transforms;

[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ThirdPersonCharacterVariableUpdateSystem))]
public partial struct CharacterAnimationWriteSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<KinematicCharacterBody, ThirdPersonCharacterControl, LocalTransform, CharacterAnimationComponent>()
                .Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        float dt = SystemAPI.Time.DeltaTime;

        foreach (var (body, ctrl, anim) in SystemAPI
                 .Query<RefRO<KinematicCharacterBody>, RefRO<ThirdPersonCharacterControl>, RefRW<CharacterAnimationComponent>>())
        {
            float3 v = body.ValueRO.RelativeVelocity;
            float speedPlanar = math.length(new float3(v.x, 0, v.z));

            // 死区消掉极小抖动
            if (speedPlanar < 0.05f) speedPlanar = 0f;

            // 安全归一化和小阈值判定
            float3 mv = ctrl.ValueRO.MoveVector;
            float  m2 = math.lengthsq(mv);
            float3 moveDir = (m2 > 1e-4f) ? math.normalize(mv) : float3.zero;

            anim.ValueRW.Speed    = speedPlanar;
            anim.ValueRW.Grounded = body.ValueRO.IsGrounded;
            anim.ValueRW.Move     = moveDir;
        }
    }
}

