using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在移动完成后让 Ani 以受限角速度转向当前攻击目标
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GameplayPostMovementSystemGroup))]
    public partial struct AniAttackFacingSystem : ISystem
    {
        private ComponentLookup<LocalTransform> _transformLookup;

        /// <summary>
        /// 缓存目标变换查询并等待存在有效攻击目标的 Ani
        /// </summary>
        /// <param name="state">系统运行状态</param>
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

        /// <summary>
        /// 忽略高度差并按每秒最大转角平滑旋转到目标方向
        /// </summary>
        /// <param name="state">系统运行状态</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _transformLookup.Update(ref state);

            float deltaTime = SystemAPI.Time.DeltaTime;
            const float maxTurnSpeedDegreesPerSecond = 540f; // 限制急转速度以保持攻击表现连续

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

                float3 attackerPosition = transform.ValueRO.Position;
                float3 targetPos = _transformLookup[targetEntity].Position;

                // 攻击朝向限定在水平面，避免地形高度差造成模型倾斜
                float3 toTarget = targetPos - attackerPosition;
                toTarget.y = 0f;

                if (math.lengthsq(toTarget) < 1e-4f)
                    continue;

                float3 desiredForward = math.normalize(toTarget);
                quaternion currentRot = transform.ValueRO.Rotation;
                float3 currentForward = math.mul(currentRot, new float3(0, 0, 1));

                // 夹角用于把固定角速度换算成本帧插值比例
                float dot = math.clamp(math.dot(currentForward, desiredForward), -1f, 1f);
                float angleDeg = math.degrees(math.acos(dot));

                if (angleDeg < 0.1f)
                    continue; // 微小误差不再写回旋转，降低抖动

                // 最大步进随帧时间缩放，保证不同帧率下角速度一致
                float maxStepDeg = maxTurnSpeedDegreesPerSecond * deltaTime;

                // 插值比例限制在零到一，避免越过目标方向
                float t = math.saturate(maxStepDeg / angleDeg);

                quaternion targetRot = quaternion.LookRotationSafe(desiredForward, math.up());
                quaternion newRot    = math.slerp(currentRot, targetRot, t);

                transform.ValueRW.Rotation = newRot;
            }
        }
    }
}
