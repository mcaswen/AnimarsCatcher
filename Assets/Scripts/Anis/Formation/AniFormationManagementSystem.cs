using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

/// <summary>
/// 在服务器处理阵型加入、离开和换队请求，并保证队长内槽位唯一
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct AniFormationManagementSystem : ISystem
{
    /// <summary>
    /// 仅在存在待处理的阵型结构变更时运行
    /// </summary>
    /// <param name="state">系统运行状态</param>
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(SystemAPI.QueryBuilder()
            .WithAny<AniFormationJoinRequest, AniFormationLeaveRequest>()
            .Build());
    }

    /// <summary>
    /// 先释放旧占用再分配最小可用槽位，保证同帧换队不会冲突
    /// </summary>
    /// <param name="state">系统运行状态</param>
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
            var m = member.ValueRO;
            slotsByLeader.Add(m.leader, m.slotIndex);
        }

        // 离开请求只负责解除成员关系并消费请求组件
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

    /// <summary>
    /// 为队长分配最小可用槽位并立即登记，避免同帧后续请求获得重复槽位
    /// </summary>
    /// <param name="leader">需要分配槽位的队长实体</param>
    /// <param name="slotsByLeader">本帧已确认的槽位占用表</param>
    /// <returns>从零开始的最小可用槽位</returns>
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
