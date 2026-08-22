using AnimarsCatcher.Gameplay;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 保存服务器确认的选择集版本，确保移动命令只引用已发布选择集
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct ClientAniSelectionAckSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            // 客户端只保留一份提交与确认状态，供选择发送和移动发送共同读取
            Entity stateEntity = state.EntityManager.CreateEntity();
            state.EntityManager.AddComponentData(stateEntity, new ClientAniSelectionSetState());
        }

        public void OnUpdate(ref SystemState state)
        {
            // Submitted 表示最近发出的版本，Acknowledged 表示服务器已完整发布的版本
            RefRW<ClientAniSelectionSetState> selection =
                SystemAPI.GetSingletonRW<ClientAniSelectionSetState>();
            // 延迟销毁接收 RPC，避免在 SystemAPI 查询期间直接结构变更
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            // 回执只在 Client World 出现，每个 Entity 消费一次后立即回收
            foreach (var (ack, ackEntity) in
                     SystemAPI.Query<RefRO<AniSelectionAckRpc>>()
                              .WithAll<ReceiveRpcCommandRequest>()
                              .WithEntityAccess())
            {
                // 三项全部匹配才确认，旧回执或伪造的不完整回执不能解锁移动命令
                if (ack.ValueRO.Version == selection.ValueRO.SubmittedVersion &&
                    ack.ValueRO.SelectionHash == selection.ValueRO.SubmittedHash &&
                    ack.ValueRO.MemberCount == selection.ValueRO.SubmittedMemberCount)
                {
                    // 确认状态完整复制服务器结果，发送移动命令时再次整体比较
                    selection.ValueRW.AcknowledgedVersion = ack.ValueRO.Version;
                    selection.ValueRW.AcknowledgedHash = ack.ValueRO.SelectionHash;
                    selection.ValueRW.AcknowledgedMemberCount = ack.ValueRO.MemberCount;
                }

                // 不匹配回执也必须销毁，否则会在之后每次更新重复检查
                entityCommandBuffer.DestroyEntity(ackEntity);
            }

            // 所有回执遍历完成后一次性提交结构变更
            entityCommandBuffer.Playback(state.EntityManager);
            entityCommandBuffer.Dispose();
        }
    }
}
