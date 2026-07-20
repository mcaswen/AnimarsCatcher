using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在服务器销毁生命值耗尽且没有专用死亡流程的 Ani 实体
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ServerApplyDamageSystem))]
    public partial struct ServerAniDeathSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate(
                SystemAPI.QueryBuilder()
                    .WithAll<Health>()
                    .Build());
        }

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
                if (healthData.Current > 0)
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
}
