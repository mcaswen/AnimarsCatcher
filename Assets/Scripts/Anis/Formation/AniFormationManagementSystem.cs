using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct AniFormationManagementSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(SystemAPI.QueryBuilder()
            .WithAny<AniFormationJoinRequest, AniFormationLeaveRequest>()
            .Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        // 先构建「当前仍然在阵列里」的槽位占用表
        // 排除：本帧打了 LeaveRequest 的；以及本帧打了 JoinRequest
        var slotsByLeader =
            new NativeParallelMultiHashMap<Entity, int>(128, Allocator.Temp);

        foreach (var member in SystemAPI
                     .Query<RefRO<AniFormationMember>>()
                     .WithNone<AniFormationLeaveRequest, AniFormationJoinRequest>())
        {
            var m = member.ValueRO;
            slotsByLeader.Add(m.leader, m.slotIndex);
        }

        // 处理离开请求：直接移除组件，不再占位
        foreach (var (leaveReq, member, entity) in SystemAPI
                     .Query<RefRO<AniFormationLeaveRequest>, RefRO<AniFormationMember>>()
                     .WithEntityAccess())
        {
            entityCommandBuffer.RemoveComponent<AniFormationMember>(entity);
            entityCommandBuffer.RemoveComponent<AniFormationLeaveRequest>(entity);
        }

        // 处理加入 / 变更编队请求
        foreach (var (joinReq, entity) in SystemAPI
                     .Query<RefRO<AniFormationJoinRequest>>()
                     .WithEntityAccess())
        {
            Entity leader = joinReq.ValueRO.leader;

            // 如果这个实体已经在某个阵列里了，说明是变更阵列请求
            // 旧的占用我们前面没塞进 slotsByLeader（因为 WithNone< JoinRequest >），
            // 等于默认已经释放出旧槽位了
            int slotIndex = AllocateSlotForLeader(leader, ref slotsByLeader);

            if (SystemAPI.HasComponent<AniFormationMember>(entity))
            {
                entityCommandBuffer.SetComponent(entity, new AniFormationMember
                {
                    leader   = leader,
                    slotIndex = slotIndex
                });
            }
            else
            {
                entityCommandBuffer.AddComponent(entity, new AniFormationMember
                {
                    leader   = leader,
                    slotIndex = slotIndex
                });
            }

            entityCommandBuffer.RemoveComponent<AniFormationJoinRequest>(entity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
        slotsByLeader.Dispose();
    }

    /// 为某个 leader 分配当前最小可用的槽位索引（0,1,2,...），
    /// 同时把结果写回 slotsByLeader，保证本帧后续的分配不会冲突
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
