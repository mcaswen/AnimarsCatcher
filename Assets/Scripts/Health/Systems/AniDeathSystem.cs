using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

/// <summary>
/// 在服务器销毁生命值耗尽且没有专用死亡流程的 Ani 实体
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ApplyDamageSystem))]
[UpdateAfter(typeof(AniAttackTargetCleanupSystem))]  // 确保目标清理和伤害结算先完成
public partial struct AniDeathSystem : ISystem
{
    /// <summary>
    /// 等待世界中存在生命值组件后再启用系统
    /// </summary>
    /// <param name="state">系统运行状态</param>
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<Health>()
                .Build());
    }

    /// <summary>
    /// 销毁普通死亡实体并把基地与资源留给各自的专用系统
    /// </summary>
    /// <param name="state">系统运行状态</param>
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (health, entity) in
                 SystemAPI.Query<RefRO<Health>>()
                     .WithEntityAccess())
        {
            var healthData = health.ValueRO;

            // 只处理本帧伤害结算后生命值耗尽的实体
            if (healthData.current > 0)
                continue;

            // 基地和脆弱资源具有独立的胜负或资源生命周期
            if (SystemAPI.HasComponent<FragileCrystal>(entity) ||
                SystemAPI.HasComponent<BigBaseTag>(entity))
            {
                continue;
            }
            ecb.DestroyEntity(entity);
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}
