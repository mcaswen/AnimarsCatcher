using AnimarsCatcher.Gameplay;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 在客户端把新点击结果和本地选择集封装为一次移动 RPC
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ClientSendAniCommandRpcSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<WorldCommandRaycastResult>();
            state.RequireForUpdate<WorldCommandSentVersion>();
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            WorldCommandRaycastResult result = SystemAPI.GetSingleton<WorldCommandRaycastResult>();
            RefRW<WorldCommandSentVersion> processed =
                SystemAPI.GetSingletonRW<WorldCommandSentVersion>();

            if (result.Version == 0 || result.Version == processed.ValueRO.Version)
                return;

            processed.ValueRW.Version = result.Version;

            if (result.TargetKind == WorldCommandTargetKind.None)
                return;

            // 客户端世界只维护一条到服务器的游戏连接
            Entity connection = SystemAPI.GetSingletonEntity<NetworkStreamInGame>();
            int localNetworkId = SystemAPI.GetComponent<NetworkId>(connection).Value;

            // 选择集使用 GhostId 快照，避免 RPC 到达前本地选择变化影响命令
            var selectedAniGhostIds = new FixedList128Bytes<int>();

            foreach (var (ghostInstance, owner) in
                    SystemAPI.Query<RefRO<GhostInstance>, RefRO<GhostOwner>>()
                            .WithAll<AniSelectedTag>()
                            .WithNone<AniCommandLockedTag>())
            {
                // 客户端只能请求控制 GhostOwner 属于自己的 Ani
                if (owner.ValueRO.NetworkId != localNetworkId)
                    continue;

                if (selectedAniGhostIds.Length >= selectedAniGhostIds.Capacity)
                    break;

                selectedAniGhostIds.Add(ghostInstance.ValueRO.ghostId);
            }

            // 空选择不创建无意义的网络消息
            if (selectedAniGhostIds.Length == 0)
                return;

            // RPC 只携带命令输入，最终权限和目标有效性由服务器复核
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            Entity rpcEntity = entityCommandBuffer.CreateEntity();
            entityCommandBuffer.AddComponent(rpcEntity, new AniCommandRpc
            {
                TargetKind          = result.TargetKind,
                TargetWorldPosition = result.TargetWorldPosition,
                TargetEntity        = result.TargetEntity,
                SelectedAniGhostIds = selectedAniGhostIds
            });

            entityCommandBuffer.AddComponent(rpcEntity, new SendRpcCommandRequest
            {
                TargetConnection = connection
            });

            entityCommandBuffer.Playback(state.EntityManager);
        }
    }
}
