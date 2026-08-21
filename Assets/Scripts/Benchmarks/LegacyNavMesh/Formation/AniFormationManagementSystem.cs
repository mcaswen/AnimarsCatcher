using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 在服务器处理阵型加入、离开和换队请求，并保证队长内槽位唯一
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct AniFormationManagementSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<LegacyNavMeshBackendEnabled>();
            state.RequireForUpdate(SystemAPI.QueryBuilder()
                .WithAny<AniFormationJoinRequest, AniFormationLeaveRequest>()
                .Build());
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            // 排除本帧离开或换队的成员，使其旧槽位可立即复用
            var slotsByLeader =
                new NativeParallelMultiHashMap<Entity, int>(128, Allocator.Temp);

            foreach (var member in SystemAPI
                         .Query<RefRO<AniFormationMember>>()
                         .WithNone<AniFormationLeaveRequest, AniFormationJoinRequest>())
            {
                var formationMember = member.ValueRO;
                slotsByLeader.Add(formationMember.leader, formationMember.slotIndex);
            }

            // 离开请求只负责解除成员关系，并在处理后移除请求组件
            foreach (var (leaveReq,  entity) in SystemAPI
                         .Query<RefRO<AniFormationLeaveRequest>>()
                         .WithEntityAccess())
            {
                if (SystemAPI.HasComponent<AniFormationMember>(entity))
                {
                    entityCommandBuffer.RemoveComponent<AniFormationMember>(entity);
                }

                entityCommandBuffer.RemoveComponent<AniFormationLeaveRequest>(entity);
            }

            // 加入和换队使用同一流程写入新的队长及槽位
            foreach (var (joinReq, entity) in SystemAPI
                         .Query<RefRO<AniFormationJoinRequest>>()
                         .WithEntityAccess())
            {
                Entity leader = joinReq.ValueRO.leader;

                // 带 JoinRequest 的旧成员未计入占用表，因此换队前旧槽位已视为释放
                int slotIndex = AllocateSlotForLeader(leader, ref slotsByLeader);

                if (SystemAPI.HasComponent<AniFormationMember>(entity))
                {
                    entityCommandBuffer.SetComponent(entity, new AniFormationMember
                    {
                        leader = leader,
                        slotIndex = slotIndex
                    });
                }
                else
                {
                    entityCommandBuffer.AddComponent(entity, new AniFormationMember
                    {
                        leader = leader,
                        slotIndex = slotIndex
                    });
                }

                entityCommandBuffer.RemoveComponent<AniFormationJoinRequest>(entity);
            }

            entityCommandBuffer.Playback(state.EntityManager);
            entityCommandBuffer.Dispose();
            slotsByLeader.Dispose();
        }

        // 分配最小可用槽位并立即登记，避免同帧后续请求获得重复槽位
        private static int AllocateSlotForLeader(
            Entity leader,
            ref NativeParallelMultiHashMap<Entity, int> slotsByLeader)
        {
            int candidate = 0;

            while (true)
            {
                bool used = false;

                NativeParallelMultiHashMapIterator<Entity> it;
                int value;

                if (slotsByLeader.TryGetFirstValue(leader, out value, out it))
                {
                    do
                    {
                        if (value == candidate)
                        {
                            used = true;
                            break;
                        }

                    } while (slotsByLeader.TryGetNextValue(out value, ref it));
                }

                if (!used)
                    break;

                candidate++;
            }

            slotsByLeader.Add(leader, candidate);
            return candidate;
        }
    }
}
