using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
// 确保在位移计算之后再改朝向（根据你项目里的实际系统名字调整）
[UpdateAfter(typeof(NavFollowIntentSystem))]
[UpdateAfter(typeof(AniPhysicsMoveSystem))]
public partial struct AniAttackFacingSystem : ISystem
{
    private ComponentLookup<LocalTransform> _transformLookup;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        _transformLookup = state.GetComponentLookup<LocalTransform>(isReadOnly: true);

        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<LocalTransform, AniAttackTarget>()
                .WithAny<PickerAniTag, BlasterAniTag>()
                .Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        _transformLookup.Update(ref state);

        float deltaTime = SystemAPI.Time.DeltaTime;
        const float maxTurnSpeedDegPerSec = 540f; // 每秒最多转 540 度（1.5 圈）

        foreach (var (transform, attackTarget, entity) in
                 SystemAPI.Query<RefRW<LocalTransform>, RefRO<AniAttackTarget>>()
                     .WithAny<PickerAniTag, BlasterAniTag>()
                     .WithEntityAccess())
        {
            Entity targetEntity = attackTarget.ValueRO.Target;

            if (targetEntity == Entity.Null)
                continue;

            if (!_transformLookup.HasComponent(targetEntity))
                continue;

            float3 myPos     = transform.ValueRO.Position;
            float3 targetPos = _transformLookup[targetEntity].Position;

            // 只在水平面上旋转，忽略高度差
            float3 toTarget = targetPos - myPos;
            toTarget.y = 0f;

            if (math.lengthsq(toTarget) < 1e-4f)
                continue;

            float3 desiredForward = math.normalize(toTarget);
            quaternion currentRot = transform.ValueRO.Rotation;
            float3 currentForward = math.mul(currentRot, new float3(0, 0, 1));

            // 当前朝向和目标朝向的夹角
            float dot = math.clamp(math.dot(currentForward, desiredForward), -1f, 1f);
            float angleDeg = math.degrees(math.acos(dot));

            if (angleDeg < 0.1f)
                continue; // 已经几乎对准了

            // 本帧最多能转多少角度
            float maxStepDeg = maxTurnSpeedDegPerSec * deltaTime;

            // 计算这帧插值因子（0~1）
            float t = math.saturate(maxStepDeg / angleDeg);

            quaternion targetRot = quaternion.LookRotationSafe(desiredForward, math.up());
            quaternion newRot    = math.slerp(currentRot, targetRot, t);

            transform.ValueRW.Rotation = newRot;
        }
    }
}
