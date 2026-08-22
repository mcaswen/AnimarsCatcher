using AnimarsCatcher.Gameplay;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 将框选结果排序去重后按协议容量拆成多个选择集 RPC
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ClientAniSelectionAckSystem))]
    [UpdateBefore(typeof(ClientSendAniCommandRpcSystem))]
    public partial struct ClientSendAniSelectionRpcSystem : ISystem
    {
        // 用于判断候选 Ani 是否属于采集类型
        private ComponentLookup<PickerAniTag> _pickerLookup;
        // 用于判断候选 Ani 是否属于战斗类型
        private ComponentLookup<BlasterAniTag> _blasterLookup;

        public void OnCreate(ref SystemState state)
        {
            // Lookup 只读取组件是否存在，不修改 Ani 类型
            _pickerLookup = state.GetComponentLookup<PickerAniTag>(true);
            _blasterLookup = state.GetComponentLookup<BlasterAniTag>(true);

            // 只有选择状态、连接和客户端协议状态齐全时才处理框选
            state.RequireForUpdate<AniSelectionDragState>();
            state.RequireForUpdate<NetworkStreamInGame>();
            state.RequireForUpdate<AniSelectionModeState>();
            state.RequireForUpdate<ClientAniSelectionSetState>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // ComponentLookup 必须在每次查询当前 World 前刷新版本
            _pickerLookup.Update(ref state);
            _blasterLookup.Update(ref state);

            RefRW<AniSelectionDragState> drag =
                SystemAPI.GetSingletonRW<AniSelectionDragState>();
            // 拖拽尚未释放时不重复扫描全部可选 Ani
            if (drag.ValueRO.IsReleased == 0)
            {
                return;
            }

            // 释放事件只消费一次，即使相机暂时不可用也不会延迟到后续误触发
            drag.ValueRW.IsReleased = 0;
            Camera camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            // 对角拖拽也归一化为屏幕空间最小角和最大角
            float2 minimum = math.min(drag.ValueRO.StartScreen, drag.ValueRO.EndScreen);
            float2 maximum = math.max(drag.ValueRO.StartScreen, drag.ValueRO.EndScreen);
            AniSelectionMode mode = SystemAPI.GetSingleton<AniSelectionModeState>().Mode;
            Entity connection = SystemAPI.GetSingletonEntity<NetworkStreamInGame>();
            // 只允许本地连接选择自己拥有的 Ani
            int localNetworkId = SystemAPI.GetComponent<NetworkId>(connection).Value;
            // 候选列表只存网络稳定的 GhostId，不把客户端 Entity 发给服务器
            using var candidates = new NativeList<int>(Allocator.Temp);

            // 框选发生时才遍历 Ani，普通帧不会承担投影成本
            foreach (var (localToWorld, ghost, owner, ani) in
                     SystemAPI.Query<RefRO<LocalToWorld>, RefRO<GhostInstance>, RefRO<GhostOwner>>()
                              .WithAll<AniAttributes>()
                              .WithEntityAccess())
            {
                // 类型过滤和所有权过滤先执行，减少不必要的屏幕投影
                bool typeMatches = mode == AniSelectionMode.Picker
                    ? _pickerLookup.HasComponent(ani)
                    : _blasterLookup.HasComponent(ani);
                if (!typeMatches || owner.ValueRO.NetworkId != localNetworkId)
                {
                    continue;
                }

                // 使用当前表现位置投影到屏幕，镜头背后的 Ani 不参与框选
                Vector3 screenPoint = camera.WorldToScreenPoint(
                    (Vector3)localToWorld.ValueRO.Position);
                if (screenPoint.z < 0f ||
                    screenPoint.x < minimum.x || screenPoint.x > maximum.x ||
                    screenPoint.y < minimum.y || screenPoint.y > maximum.y)
                {
                    continue;
                }

                // GhostId 可能因异常数据重复，稍后统一排序去重
                candidates.Add(ghost.ValueRO.ghostId);
            }

            // 排序同时提供确定性协议顺序和线性去重条件
            candidates.AsArray().Sort();
            // 容量最多扩到协议上限，超出范围的候选按 GhostId 顺序截断
            using var selectedGhostIds = new NativeList<int>(
                math.max(1, math.min(candidates.Length, AniSelectionProtocol.MaximumMemberCount)),
                Allocator.Temp);
            for (int index = 0;
                 index < candidates.Length &&
                 selectedGhostIds.Length < AniSelectionProtocol.MaximumMemberCount;
                 index++)
            {
                // 排序后只需比较前一项即可消除重复 GhostId
                if (index == 0 || candidates[index] != candidates[index - 1])
                {
                    selectedGhostIds.Add(candidates[index]);
                }
            }

            RefRW<ClientAniSelectionSetState> selection =
                SystemAPI.GetSingletonRW<ClientAniSelectionSetState>();
            // 每次释放框选都创建新版本，包括框选结果为空的 Clear
            uint version = NextVersion(selection.ValueRO.SubmittedVersion);
            // Hash 覆盖完整最终结果，服务器收齐分块后必须得到同一值
            ulong resultHash = AniSelectionProtocol.ComputeSelectionHash(
                version,
                selectedGhostIds.AsArray());
            // 空选择仍发送一个空块，让服务器能够明确发布 Clear
            int chunkCount = math.max(
                1,
                (selectedGhostIds.Length + AniSelectionProtocol.MemberIdsPerChunk - 1) /
                AniSelectionProtocol.MemberIdsPerChunk);
            // 一次框选的所有 RPC 在同一个 ECB 中创建，保持提交边界清晰
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            for (ushort chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                // FixedList 只承载单块成员，完整万人集合不会复制到一个 RPC
                FixedList512Bytes<int> chunkGhostIds = default;
                // 每块最多写入 120 个，最后一块按剩余数量收尾
                int start = chunkIndex * AniSelectionProtocol.MemberIdsPerChunk;
                int end = math.min(
                    start + AniSelectionProtocol.MemberIdsPerChunk,
                    selectedGhostIds.Length);
                for (int memberIndex = start; memberIndex < end; memberIndex++)
                {
                    chunkGhostIds.Add(selectedGhostIds[memberIndex]);
                }

                // 当前框选只使用 Replace 或 Clear，协议仍为后续增量操作保留模式
                var rpc = new AniSelectionChunkRpc
                {
                    Version = version,
                    Mode = selectedGhostIds.IsEmpty
                        ? AniSelectionUpdateMode.Clear
                        : AniSelectionUpdateMode.Replace,
                    ChunkIndex = chunkIndex,
                    ChunkCount = (ushort)chunkCount,
                    PayloadMemberCount = selectedGhostIds.Length,
                    ResultMemberCount = selectedGhostIds.Length,
                    ResultHash = resultHash,
                    GhostIds = chunkGhostIds,
                };
                // ChunkHash 在成员写完后计算，用于服务器识别重传和冲突
                rpc.ChunkHash = AniSelectionProtocol.ComputeChunkHash(
                    rpc.Version,
                    rpc.ChunkIndex,
                    rpc.ChunkCount,
                    rpc.GhostIds);

                // 每个分块独立成为可靠 RPC Entity，并定向发送到当前连接
                Entity rpcEntity = entityCommandBuffer.CreateEntity();
                entityCommandBuffer.AddComponent(rpcEntity, rpc);
                entityCommandBuffer.AddComponent(rpcEntity, new SendRpcCommandRequest
                {
                    TargetConnection = connection,
                });
            }

            // 先记录提交态并清空旧确认，移动命令必须等待同版本回执
            selection.ValueRW.SubmittedVersion = version;
            selection.ValueRW.SubmittedHash = resultHash;
            selection.ValueRW.SubmittedMemberCount = selectedGhostIds.Length;
            selection.ValueRW.AcknowledgedVersion = 0;
            selection.ValueRW.AcknowledgedHash = 0;
            selection.ValueRW.AcknowledgedMemberCount = 0;

            // 状态写入完成后统一创建 RPC Entity
            entityCommandBuffer.Playback(state.EntityManager);
            entityCommandBuffer.Dispose();
        }

        private static uint NextVersion(uint current)
        {
            // 零值表示尚未提交，版本溢出后从一重新开始
            uint next = current + 1;
            return next == 0 ? 1u : next;
        }
    }
}
