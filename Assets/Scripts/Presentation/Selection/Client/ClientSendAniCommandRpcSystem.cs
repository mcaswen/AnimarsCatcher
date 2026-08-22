using AnimarsCatcher.Gameplay;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 在客户端把新点击结果和服务器已确认的选择集版本封装为移动 RPC
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
            state.RequireForUpdate<ClientAniSelectionSetState>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            WorldCommandRaycastResult result = SystemAPI.GetSingleton<WorldCommandRaycastResult>();
            RefRW<WorldCommandSentVersion> processed =
                SystemAPI.GetSingletonRW<WorldCommandSentVersion>();

            if (result.Version == 0 || result.Version == processed.ValueRO.Version)
                return;

            if (result.TargetKind == WorldCommandTargetKind.None)
            {
                processed.ValueRW.Version = result.Version;
                return;
            }

            ClientAniSelectionSetState selection =
                SystemAPI.GetSingleton<ClientAniSelectionSetState>();
            bool selectionIsAcknowledged =
                selection.SubmittedVersion != 0 &&
                selection.SubmittedVersion == selection.AcknowledgedVersion &&
                selection.SubmittedHash == selection.AcknowledgedHash &&
                selection.SubmittedMemberCount == selection.AcknowledgedMemberCount;
            if (!selectionIsAcknowledged)
            {
                return;
            }

            if (selection.AcknowledgedMemberCount == 0)
            {
                processed.ValueRW.Version = result.Version;
                return;
            }

            // 未收到回执时保留点击版本，回执到达后会自动补发这次命令
            processed.ValueRW.Version = result.Version;
            Entity connection = SystemAPI.GetSingletonEntity<NetworkStreamInGame>();

            // RPC 只引用服务器已经发布的选择集，不再重复发送成员列表
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            Entity rpcEntity = entityCommandBuffer.CreateEntity();
            entityCommandBuffer.AddComponent(rpcEntity, new AniCommandRpc
            {
                TargetKind          = result.TargetKind,
                TargetWorldPosition = result.TargetWorldPosition,
                TargetEntity        = result.TargetEntity,
                SelectionVersion    = selection.AcknowledgedVersion,
                SelectionHash       = selection.AcknowledgedHash,
            });

            entityCommandBuffer.AddComponent(rpcEntity, new SendRpcCommandRequest
            {
                TargetConnection = connection
            });

            entityCommandBuffer.Playback(state.EntityManager);
        }
    }
}
