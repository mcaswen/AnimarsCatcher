using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在服务器组装分块选择请求，并只发布完整且有权限的玩家选择集
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(ServerAniGhostIdIndexSystem))]
    public partial class ServerAniSelectionSetSystem : SystemBase
    {
        // 查询所有尚在等待后续分块的选择集组装状态
        private EntityQuery _assemblyQuery;

        // 查询服务器统一发布的 GhostId 到 Ani 映射
        private EntityQuery _indexQuery;

        // 查询协议诊断计数，验收和运行时监控共用同一份数据
        private EntityQuery _metricsQuery;

        // 查询 NetCode 已接收但尚未消费的选择集分块
        private EntityQuery _rpcQuery;

        // 查询每个玩家最后一次完整发布的权威选择集
        private EntityQuery _selectionQuery;

        // 使用本 System 的更新序号判断不完整组装是否超时
        private uint _serverTick;

        protected override void OnCreate()
        {
            // RPC Entity 必须同时带有来源连接，服务器才能判断成员所有权
            _rpcQuery = GetEntityQuery(
                ComponentType.ReadOnly<AniSelectionChunkRpc>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());

            // 已发布选择集的 Component 和成员 Buffer 必须成对存在
            _selectionQuery = GetEntityQuery(
                ComponentType.ReadOnly<ServerAniSelectionSet>(),
                ComponentType.ReadWrite<ServerAniSelectionMember>());

            // 组装 Entity 同时保存块摘要和块内成员，支持乱序及重复检查
            _assemblyQuery = GetEntityQuery(
                ComponentType.ReadOnly<ServerAniSelectionAssembly>(),
                ComponentType.ReadWrite<ServerAniSelectionAssemblyChunk>(),
                ComponentType.ReadWrite<ServerAniSelectionAssemblyMember>());

            // 索引由前置 System 整体发布，本 System 只读
            _indexQuery = GetEntityQuery(
                ComponentType.ReadOnly<ServerAniGhostIdIndex>(),
                ComponentType.ReadOnly<ServerAniGhostIdIndexEntry>());

            // 指标使用单例 Component，避免为每个玩家分散统计
            _metricsQuery = GetEntityQuery(ComponentType.ReadWrite<ServerAniSelectionMetrics>());

            // 指标 Entity 与网络连接无关，Server World 生命周期内始终存在
            Entity metricsEntity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(metricsEntity, new ServerAniSelectionMetrics());

            // 没有 GhostId 索引时无法做权限校验，因此不消费任何选择 RPC
            RequireForUpdate(_indexQuery);
        }

        protected override void OnUpdate()
        {
            // 更新序号跳过零值，减法使用无符号环绕支持长期运行
            _serverTick = NextVersion(_serverTick);

            // 本轮先读取指标，所有分支累计后再统一写回
            Entity metricsEntity = _metricsQuery.GetSingletonEntity();
            ServerAniSelectionMetrics metrics =
                EntityManager.GetComponentData<ServerAniSelectionMetrics>(metricsEntity);

            // 清理必须早于建表，避免过期或断线状态参与本轮版本判断
            CleanupExpiredAssemblies(ref metrics);
            CleanupDisconnectedSelections();

            // 每个玩家最多映射一个已发布选择集和一个活动组装
            using var selectionsByOwner = BuildSelectionOwnerMap();
            using var assembliesByOwner = BuildAssemblyOwnerMap();

            // 复制索引后再执行创建和销毁 Entity，防止结构变更使 DynamicBuffer 引用失效
            Entity indexEntity = _indexQuery.GetSingletonEntity();
            using NativeArray<ServerAniGhostIdIndexEntry> ghostIndex =
                EntityManager.GetBuffer<ServerAniGhostIdIndexEntry>(indexEntity, true)
                    .ToNativeArray(Allocator.Temp);

            // RPC Entity 先快照再遍历，消费过程中可以立即销毁原 Entity
            using NativeArray<Entity> rpcEntities = _rpcQuery.ToEntityArray(Allocator.Temp);

            for (int rpcIndex = 0; rpcIndex < rpcEntities.Length; rpcIndex++)
            {
                Entity rpcEntity = rpcEntities[rpcIndex];
                // 同一帧其他逻辑可能已经消费或销毁 RPC，失效项直接跳过
                if (!EntityManager.Exists(rpcEntity))
                {
                    continue;
                }

                AniSelectionChunkRpc rpc =
                    EntityManager.GetComponentData<AniSelectionChunkRpc>(rpcEntity);
                ReceiveRpcCommandRequest receive =
                    EntityManager.GetComponentData<ReceiveRpcCommandRequest>(rpcEntity);

                // 读取完成后立即销毁，任何拒绝分支都不会重复处理同一个网络包
                EntityManager.DestroyEntity(rpcEntity);

                // 来源连接不存在或没有 NetworkId 时无法建立可信玩家身份
                if (!EntityManager.Exists(receive.SourceConnection) ||
                    !EntityManager.HasComponent<NetworkId>(receive.SourceConnection))
                {
                    metrics.RejectedUnauthorizedCount++;
                    continue;
                }

                int ownerNetworkId =
                    EntityManager.GetComponentData<NetworkId>(receive.SourceConnection).Value;

                // 包络先于任何 Buffer 写入校验，阻止非法长度扩大临时状态
                if (!ValidateEnvelope(rpc))
                {
                    // 格式错误计数只记录请求，不把其中声明的成员视为可信数据
                    metrics.RejectedMalformedCount++;

                    // 同版本已接收的块也不再可信，需要一起丢弃
                    RejectMatchingAssembly(
                        ownerNetworkId,
                        rpc.Version,
                        assembliesByOwner,
                        ref metrics,
                        countAsIncomplete: false);
                    continue;
                }

                // 服务器已经发布的版本不可覆盖，也不需要为重放发送第二次确认
                if (selectionsByOwner.TryGetValue(ownerNetworkId, out Entity selectionEntity))
                {
                    ServerAniSelectionSet current =
                        EntityManager.GetComponentData<ServerAniSelectionSet>(selectionEntity);
                    if (rpc.Version <= current.Version)
                    {
                        // 当前权威选择保持不变，旧包不能回滚选择状态
                        metrics.RejectedStaleCount++;
                        continue;
                    }
                }

                Entity assemblyEntity;
                // 同一玩家只允许一个版本处于组装中，避免并行版本争夺最终状态
                if (assembliesByOwner.TryGetValue(ownerNetworkId, out Entity activeAssembly))
                {
                    ServerAniSelectionAssembly active =
                        EntityManager.GetComponentData<ServerAniSelectionAssembly>(activeAssembly);

                    // 比活动组装更旧的块不会影响当前进度
                    if (rpc.Version < active.Version)
                    {
                        // 正在组装的较新版本继续等待剩余分块
                        metrics.RejectedStaleCount++;
                        continue;
                    }

                    if (rpc.Version > active.Version)
                    {
                        // 新版本代表客户端已经放弃旧版本，未收齐状态立即回收
                        EntityManager.DestroyEntity(activeAssembly);
                        assembliesByOwner.Remove(ownerNetworkId);
                        // 被替代版本没有发布，计入未完成而不是格式错误
                        metrics.RejectedIncompleteCount++;
                        assemblyEntity = CreateAssembly(receive.SourceConnection, ownerNetworkId, rpc);
                        assembliesByOwner.TryAdd(ownerNetworkId, assemblyEntity);
                    }
                    else
                    {
                        // 同版本每个块必须声明完全一致的模式、总量和结果
                        if (!MetadataMatches(active, rpc))
                        {
                            // 公共元数据冲突会使同版本含义不唯一，不能继续组装
                            EntityManager.DestroyEntity(activeAssembly);
                            assembliesByOwner.Remove(ownerNetworkId);
                            metrics.RejectedMalformedCount++;
                            continue;
                        }

                        assemblyEntity = activeAssembly;
                    }
                }
                else
                {
                    // 第一个合法块建立组装 Entity，块序号不要求从零开始
                    assemblyEntity = CreateAssembly(receive.SourceConnection, ownerNetworkId, rpc);
                    assembliesByOwner.TryAdd(ownerNetworkId, assemblyEntity);
                }

                DynamicBuffer<ServerAniSelectionAssemblyChunk> chunks =
                    EntityManager.GetBuffer<ServerAniSelectionAssemblyChunk>(assemblyEntity);
                DynamicBuffer<ServerAniSelectionAssemblyMember> stagedMembers =
                    EntityManager.GetBuffer<ServerAniSelectionAssemblyMember>(assemblyEntity);

                // 块摘要表只保存已接收位置，成员内容存放在另一 Buffer
                int existingChunkIndex = FindChunk(chunks, rpc.ChunkIndex);
                if (existingChunkIndex >= 0)
                {
                    // 相同位置必须同时满足 Hash 和逐成员一致才算网络重传
                    bool exactDuplicate = chunks[existingChunkIndex].ChunkHash == rpc.ChunkHash &&
                                          ChunkMembersMatch(stagedMembers, rpc);
                    if (exactDuplicate)
                    {
                        // 幂等重传不增加已收块数，也不会刷新最终选择集
                        metrics.IgnoredDuplicateChunkCount++;
                    }
                    else
                    {
                        // 同位置出现不同内容时无法判断哪一块可信，整个版本作废
                        EntityManager.DestroyEntity(assemblyEntity);
                        assembliesByOwner.Remove(ownerNetworkId);
                        metrics.RejectedDuplicateCount++;
                    }

                    continue;
                }

                // 首次出现的块先登记摘要，再按块内原始顺序保存成员
                chunks.Add(new ServerAniSelectionAssemblyChunk
                {
                    ChunkIndex = rpc.ChunkIndex,
                    ChunkHash = rpc.ChunkHash,
                });
                for (ushort memberIndex = 0; memberIndex < rpc.GhostIds.Length; memberIndex++)
                {
                    stagedMembers.Add(new ServerAniSelectionAssemblyMember
                    {
                        ChunkIndex = rpc.ChunkIndex,
                        MemberIndex = memberIndex,
                        GhostId = rpc.GhostIds[memberIndex],
                    });
                }

                ServerAniSelectionAssembly assembly =
                    EntityManager.GetComponentData<ServerAniSelectionAssembly>(assemblyEntity);

                // 只有唯一新块会推进计数和最近接收时间
                assembly.ReceivedChunkCount++;
                assembly.LastReceivedTick = _serverTick;
                EntityManager.SetComponentData(assemblyEntity, assembly);

                // 未收齐时保留组装状态，绝不让部分成员进入权威选择集
                if (assembly.ReceivedChunkCount != assembly.ChunkCount)
                {
                    continue;
                }

                // 收齐只代表块数量完整，发布前仍需校验成员、权限和结果 Hash
                bool published = TryPublishSelection(
                    assemblyEntity,
                    assembly,
                    ghostIndex,
                    selectionsByOwner,
                    ref metrics);

                // 无论最终校验成功还是失败，本版本的临时组装都已经结束
                EntityManager.DestroyEntity(assemblyEntity);
                assembliesByOwner.Remove(ownerNetworkId);

                if (published)
                {
                    // 只确认已经原子发布的版本，客户端才能安全引用它发送移动命令
                    SendAck(receive.SourceConnection, assembly);
                }
            }

            // 指标最后统一提交，保证本轮所有拒绝路径都被保留
            EntityManager.SetComponentData(metricsEntity, metrics);
        }

        private bool TryPublishSelection(
            Entity assemblyEntity,
            ServerAniSelectionAssembly assembly,
            NativeArray<ServerAniGhostIdIndexEntry> ghostIndex,
            NativeParallelHashMap<int, Entity> selectionsByOwner,
            ref ServerAniSelectionMetrics metrics)
        {
            // 组装成员数必须与协议声明一致，块齐全也不能掩盖成员缺失
            DynamicBuffer<ServerAniSelectionAssemblyMember> staged =
                EntityManager.GetBuffer<ServerAniSelectionAssemblyMember>(assemblyEntity);
                if (staged.Length != assembly.PayloadMemberCount)
                {
                    // 实际成员总量不符通常意味着缺块声明或异常重复内容
                    metrics.RejectedIncompleteCount++;
                return false;
            }

            // payload 保存本次提交成员，排序后用于重复和权限校验
            var payload = new NativeList<int>(
                staged.Length > 0 ? staged.Length : 1,
                Allocator.Temp);

            // previousIds 保存上一版有序成员，供 Add 和 Remove 做线性归并
            var previousIds = new NativeList<int>(Allocator.Temp);

            // result 是应用更新模式后的最终 GhostId 快照
            var result = new NativeList<int>(
                assembly.ResultMemberCount > 0 ? assembly.ResultMemberCount : 1,
                Allocator.Temp);

            // resolved 同时冻结 GhostId 和当前服务器 Entity，发布时无需再次查表
            var resolved = new NativeList<ServerAniSelectionMember>(
                assembly.ResultMemberCount > 0 ? assembly.ResultMemberCount : 1,
                Allocator.Temp);
            try
            {
                // 分块到达顺序不具备业务含义，先提取全部 GhostId
                for (int index = 0; index < staged.Length; index++)
                {
                    payload.Add(staged[index].GhostId);
                }

                // 统一排序使顺序、逆序和混合到达都产生相同选择结果
                payload.AsArray().Sort();

                // 排序后相同成员必然相邻，可以用一次线性扫描拒绝重复
                for (int index = 1; index < payload.Length; index++)
                {
                    if (payload[index] == payload[index - 1])
                    {
                        // 重复成员会破坏集合语义和成员数量声明
                        metrics.RejectedDuplicateCount++;
                        return false;
                    }
                }

                // payload 中的每个 Ani 都必须存在并属于提交连接
                for (int index = 0; index < payload.Length; index++)
                {
                    if (!ServerAniGhostIdIndexSystem.TryResolve(
                            ghostIndex,
                            payload[index],
                            out ServerAniGhostIdIndexEntry entry) ||
                        entry.OwnerNetworkId != assembly.OwnerNetworkId)
                    {
                        // 不区分不存在和越权，避免通过指标泄露其他玩家成员信息
                        metrics.RejectedUnauthorizedCount++;
                        return false;
                    }
                }

                // Clear 或首次 Replace 允许没有上一版选择集
                Entity previousSelectionEntity = Entity.Null;
                if (selectionsByOwner.TryGetValue(
                        assembly.OwnerNetworkId,
                        out previousSelectionEntity))
                {
                    // 已发布成员本身保持升序，可以直接作为归并输入
                    DynamicBuffer<ServerAniSelectionMember> previousMembers =
                        EntityManager.GetBuffer<ServerAniSelectionMember>(
                            previousSelectionEntity,
                            true);
                    // 预扩容避免万人成员逐项复制时反复增长 NativeList
                    if (previousMembers.Length > previousIds.Capacity)
                    {
                        previousIds.Capacity = previousMembers.Length;
                    }

                    // 这里只复制 GhostId，旧 Entity 引用会在最终结果阶段重新解析
                    for (int index = 0; index < previousMembers.Length; index++)
                    {
                        previousIds.Add(previousMembers[index].GhostId);
                    }
                }

                // Replace、Add、Remove 和 Clear 最终都归一化成一个有序结果
                BuildResult(assembly.Mode, previousIds.AsArray(), payload.AsArray(), result);

                // 客户端声明的数量与 Hash 必须同时匹配，防止缺块或模式理解不一致
                if (result.Length != assembly.ResultMemberCount ||
                    AniSelectionProtocol.ComputeSelectionHash(
                        assembly.Version,
                        result.AsArray()) != assembly.ResultHash)
                {
                    metrics.RejectedMalformedCount++;
                    // 结果声明不一致时不触碰上一版权威选择
                    return false;
                }

                // 再次解析最终结果，因为 Add 会保留未出现在 payload 中的旧成员
                for (int index = 0; index < result.Length; index++)
                {
                    if (!ServerAniGhostIdIndexSystem.TryResolve(
                            ghostIndex,
                            result[index],
                            out ServerAniGhostIdIndexEntry entry) ||
                        entry.OwnerNetworkId != assembly.OwnerNetworkId)
                    {
                        // 旧成员可能在组装期间销毁或易主，因此必须在发布点复核
                        metrics.RejectedUnauthorizedCount++;
                        return false;
                    }

                    // resolved 的顺序与 result 一致，发布后可直接用于二分和稳定遍历
                    resolved.Add(new ServerAniSelectionMember
                    {
                        GhostId = entry.GhostId,
                        Ani = entry.Ani,
                    });
                }

                // 玩家第一次提交时才创建权威选择集 Entity
                Entity selectionEntity = previousSelectionEntity;
                if (selectionEntity == Entity.Null)
                {
                    // 首次提交同时创建元数据 Component 和外置成员 Buffer
                    selectionEntity = EntityManager.CreateEntity();
                    EntityManager.AddComponentData(selectionEntity, new ServerAniSelectionSet());
                    EntityManager.AddBuffer<ServerAniSelectionMember>(selectionEntity);
                    selectionsByOwner.TryAdd(assembly.OwnerNetworkId, selectionEntity);
                }

                // 先关闭旧成员选择标记，再用完整新快照重新开启
                DynamicBuffer<ServerAniSelectionMember> target =
                    EntityManager.GetBuffer<ServerAniSelectionMember>(selectionEntity);
                for (int index = 0; index < target.Length; index++)
                {
                    // Entity 已销毁时 SetSelected 会安全忽略
                    SetSelected(target[index].Ani, false);
                }

                // Buffer 采用整体替换，其他 System 不会读到 Add 或 Remove 的中间结果
                target.Clear();
                target.EnsureCapacity(resolved.Length);
                for (int index = 0; index < resolved.Length; index++)
                {
                    target.Add(resolved[index]);

                    // AniSelectedTag 服务于服务器侧现有业务兼容，不承担网络选择真值
                    SetSelected(resolved[index].Ani, true);
                }

                // Component 元数据在成员 Buffer 完成后写入，形成可验证的发布边界
                EntityManager.SetComponentData(selectionEntity, new ServerAniSelectionSet
                {
                    SourceConnection = assembly.SourceConnection,
                    OwnerNetworkId = assembly.OwnerNetworkId,
                    Version = assembly.Version,
                    CompletenessHash = assembly.ResultHash,
                    MemberCount = target.Length,
                });

                // 只有完成上述全部步骤才计为成功选择版本
                metrics.CompletedSelectionCount++;
                return true;
            }
            finally
            {
                // 所有早退分支都必须释放临时容器，避免拒绝流量造成泄漏
                payload.Dispose();
                previousIds.Dispose();
                result.Dispose();
                resolved.Dispose();
            }
        }

        private static void BuildResult(
            AniSelectionUpdateMode mode,
            NativeArray<int> previous,
            NativeArray<int> payload,
            NativeList<int> result)
        {
            // Clear 的最终结果固定为空，不需要读取旧成员或 payload
            if (mode == AniSelectionUpdateMode.Clear)
            {
                // 即使调用方错误传入 payload，包络校验也会在此前拒绝
                return;
            }

            // Replace 已完成排序和唯一性校验，可以直接复制提交内容
            if (mode == AniSelectionUpdateMode.Replace)
            {
                // NativeList 接管一份值复制，不引用临时 payload 内存
                result.AddRange(payload);
                return;
            }

            // Add 和 Remove 都在两个有序集合上执行双指针归并
            int previousIndex = 0;
            int payloadIndex = 0;
            while (previousIndex < previous.Length || payloadIndex < payload.Length)
            {
                // 一侧耗尽时使用最大值哨兵，让另一侧继续自然推进
                int previousId = previousIndex < previous.Length
                    ? previous[previousIndex]
                    : int.MaxValue;
                int payloadId = payloadIndex < payload.Length
                    ? payload[payloadIndex]
                    : int.MaxValue;

                if (previousId == payloadId)
                {
                    // Add 保留交集成员，Remove 则跳过交集成员
                    if (mode == AniSelectionUpdateMode.Add)
                    {
                        result.Add(previousId);
                    }

                    // 相等时两侧同时前进，保证结果不会出现重复 GhostId
                    previousIndex++;
                    payloadIndex++;
                }
                else if (previousId < payloadId)
                {
                    // 旧成员不在本次 payload 中，Add 和 Remove 都应保留
                    result.Add(previousId);
                    previousIndex++;
                }
                else
                {
                    // 仅 payload 存在的成员只在 Add 模式进入结果
                    if (mode == AniSelectionUpdateMode.Add)
                    {
                        result.Add(payloadId);
                    }

                    payloadIndex++;
                }
            }
        }

        private Entity CreateAssembly(
            Entity sourceConnection,
            int ownerNetworkId,
            AniSelectionChunkRpc rpc)
        {
            // 组装状态复制首块公共元数据，后续块必须逐项匹配
            Entity entity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(entity, new ServerAniSelectionAssembly
            {
                SourceConnection = sourceConnection,
                OwnerNetworkId = ownerNetworkId,
                Version = rpc.Version,
                Mode = rpc.Mode,
                ChunkCount = rpc.ChunkCount,
                ReceivedChunkCount = 0,
                PayloadMemberCount = rpc.PayloadMemberCount,
                ResultMemberCount = rpc.ResultMemberCount,
                ResultHash = rpc.ResultHash,
                LastReceivedTick = _serverTick,
            });

            // 块摘要和成员分离，检查重复块时不必重排整个成员列表
            EntityManager.AddBuffer<ServerAniSelectionAssemblyChunk>(entity);
            // 成员 Buffer 初始为空，首块会在返回后由调用方写入
            EntityManager.AddBuffer<ServerAniSelectionAssemblyMember>(entity);
            return entity;
        }

        private void CleanupExpiredAssemblies(ref ServerAniSelectionMetrics metrics)
        {
            // 查询结果先复制到临时数组，遍历期间可以安全销毁过期 Entity
            using NativeArray<Entity> entities = _assemblyQuery.ToEntityArray(Allocator.Temp);
            using NativeArray<ServerAniSelectionAssembly> assemblies =
                _assemblyQuery.ToComponentDataArray<ServerAniSelectionAssembly>(Allocator.Temp);
            for (int index = 0; index < entities.Length; index++)
            {
                // 活跃连接且等待时间未超过预算时继续保留组装
                if (unchecked(_serverTick - assemblies[index].LastReceivedTick) <=
                        AniSelectionProtocol.AssemblyTimeoutTicks &&
                    ConnectionMatches(
                        assemblies[index].SourceConnection,
                        assemblies[index].OwnerNetworkId))
                {
                    continue;
                }

                // 超时和断线都视为未完成提交，不改变玩家已发布选择集
                EntityManager.DestroyEntity(entities[index]);
                // 清理只影响临时提交，不会改变客户端最后确认的版本
                metrics.RejectedIncompleteCount++;
            }
        }

        private void CleanupDisconnectedSelections()
        {
            // 权威选择集与来源连接绑定，连接失效后不能留给复用的 NetworkId
            using NativeArray<Entity> entities =
                _selectionQuery.ToEntityArray(Allocator.Temp);
            using NativeArray<ServerAniSelectionSet> selections =
                _selectionQuery.ToComponentDataArray<ServerAniSelectionSet>(Allocator.Temp);
            for (int index = 0; index < entities.Length; index++)
            {
                // 同一个连接 Entity 仍存在且 NetworkId 未变化时保持选择集
                if (ConnectionMatches(
                        selections[index].SourceConnection,
                        selections[index].OwnerNetworkId))
                {
                    continue;
                }

                // 销毁选择集前关闭所有仍存活 Ani 的兼容选择标记
                DynamicBuffer<ServerAniSelectionMember> members =
                    EntityManager.GetBuffer<ServerAniSelectionMember>(entities[index], true);
                for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
                {
                    SetSelected(members[memberIndex].Ani, false);
                }

                EntityManager.DestroyEntity(entities[index]);
                // 已发布选择集随连接销毁，不向已经断开的客户端发送 Clear
            }
        }

        private bool ConnectionMatches(Entity connection, int ownerNetworkId)
        {
            // 同时验证 Entity 存活、身份组件存在和编号一致，避免连接 Entity 复用
            return EntityManager.Exists(connection) &&
                   EntityManager.HasComponent<NetworkId>(connection) &&
                   EntityManager.GetComponentData<NetworkId>(connection).Value == ownerNetworkId;
        }

        private void RejectMatchingAssembly(
            int ownerNetworkId,
            uint version,
            NativeParallelHashMap<int, Entity> assembliesByOwner,
            ref ServerAniSelectionMetrics metrics,
            bool countAsIncomplete)
        {
            // 非法包只有在玩家确实存在活动组装时才需要清理
            if (!assembliesByOwner.TryGetValue(ownerNetworkId, out Entity entity) ||
                !EntityManager.Exists(entity))
            {
                return;
            }

            // 其他版本的非法包不能破坏玩家正在提交的较新版本
            ServerAniSelectionAssembly assembly =
                EntityManager.GetComponentData<ServerAniSelectionAssembly>(entity);
            if (assembly.Version != version)
            {
                return;
            }

            // 删除后同步更新本轮临时映射，避免后续 RPC 使用失效 Entity
            EntityManager.DestroyEntity(entity);
            assembliesByOwner.Remove(ownerNetworkId);
            if (countAsIncomplete)
            {
                // 调用方决定该拒绝是否应同时归入未完成统计
                metrics.RejectedIncompleteCount++;
            }
        }

        private void SetSelected(Entity ani, bool selected)
        {
            // Enableable Tag 只在目标仍存在且具备该组件时切换
            if (EntityManager.Exists(ani) && EntityManager.HasComponent<AniSelectedTag>(ani))
            {
                // Enableable Component 切换不产生 Entity 结构变化
                EntityManager.SetComponentEnabled<AniSelectedTag>(ani, selected);
            }
        }

        private void SendAck(Entity connection, ServerAniSelectionAssembly assembly)
        {
            // 发布后连接可能恰好断开，此时无需创建无目标回执
            if (!EntityManager.Exists(connection))
            {
                // 选择集已经发布，缺少回执只会让客户端暂不发送移动命令
                return;
            }

            // 回执仅包含最终版本、Hash 和数量，不重复回传成员列表
            Entity ackEntity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(ackEntity, new AniSelectionAckRpc
            {
                Version = assembly.Version,
                SelectionHash = assembly.ResultHash,
                MemberCount = assembly.ResultMemberCount,
            });

            // 显式绑定原来源连接，避免广播其他玩家的选择状态
            EntityManager.AddComponentData(ackEntity, new SendRpcCommandRequest
            {
                TargetConnection = connection,
            });
        }

        private static bool ValidateEnvelope(AniSelectionChunkRpc rpc)
        {
            // 枚举值来自网络输入，必须显式限制在协议定义范围内
            bool validMode = rpc.Mode >= AniSelectionUpdateMode.Replace &&
                             rpc.Mode <= AniSelectionUpdateMode.Clear;

            // 总块数必须是 payload 数量对应的最小值，禁止额外空块拖延组装
            int expectedChunkCount = math.max(
                1,
                (rpc.PayloadMemberCount + AniSelectionProtocol.MemberIdsPerChunk - 1) /
                AniSelectionProtocol.MemberIdsPerChunk);

            // 最后一块可以不足 120 个，其他块必须刚好达到协议容量
            int expectedChunkLength = math.max(
                0,
                math.min(
                    AniSelectionProtocol.MemberIdsPerChunk,
                    rpc.PayloadMemberCount -
                    rpc.ChunkIndex * AniSelectionProtocol.MemberIdsPerChunk));

            // Clear 使用一个空块表达，避免零块提交永远无法触发服务器处理
            bool clearIsEmpty = rpc.Mode != AniSelectionUpdateMode.Clear ||
                                (rpc.PayloadMemberCount == 0 &&
                                 rpc.ResultMemberCount == 0 &&
                                 rpc.GhostIds.Length == 0 &&
                                 rpc.ChunkCount == 1);

            // Replace 的 payload 就是完整结果，两种数量必须相等
            bool replaceCountMatches = rpc.Mode != AniSelectionUpdateMode.Replace ||
                                       rpc.PayloadMemberCount == rpc.ResultMemberCount;

            // 所有边界在创建组装 Entity 前一次完成，失败包不分配持久 Buffer
            return rpc.Version != 0 &&
                   validMode &&
                   rpc.ChunkCount > 0 &&
                   rpc.ChunkCount <= AniSelectionProtocol.MaximumChunkCount &&
                   rpc.ChunkCount == expectedChunkCount &&
                   rpc.ChunkIndex < rpc.ChunkCount &&
                   rpc.PayloadMemberCount >= 0 &&
                   rpc.PayloadMemberCount <= AniSelectionProtocol.MaximumMemberCount &&
                   rpc.ResultMemberCount >= 0 &&
                   rpc.ResultMemberCount <= AniSelectionProtocol.MaximumMemberCount &&
                   rpc.GhostIds.Length <= AniSelectionProtocol.MemberIdsPerChunk &&
                   rpc.GhostIds.Length == expectedChunkLength &&
                   clearIsEmpty &&
                   replaceCountMatches &&
                   rpc.ChunkHash == AniSelectionProtocol.ComputeChunkHash(
                       rpc.Version,
                       rpc.ChunkIndex,
                       rpc.ChunkCount,
                       rpc.GhostIds);
        }

        private static bool MetadataMatches(
            ServerAniSelectionAssembly assembly,
            AniSelectionChunkRpc rpc)
        {
            // 同版本只允许块位置和块内容不同，公共结果声明必须保持一致
            return assembly.Version == rpc.Version &&
                   assembly.Mode == rpc.Mode &&
                   assembly.ChunkCount == rpc.ChunkCount &&
                   assembly.PayloadMemberCount == rpc.PayloadMemberCount &&
                   assembly.ResultMemberCount == rpc.ResultMemberCount &&
                   assembly.ResultHash == rpc.ResultHash;
        }

        private static int FindChunk(
            DynamicBuffer<ServerAniSelectionAssemblyChunk> chunks,
            ushort chunkIndex)
        {
            // 最大只有 84 块，顺序扫描比为每个组装再维护 HashMap 更简单
            for (int index = 0; index < chunks.Length; index++)
            {
                if (chunks[index].ChunkIndex == chunkIndex)
                {
                    // 返回摘要 Buffer 下标，调用方再核对 Hash 和成员内容
                    return index;
                }
            }

            return -1;
        }

        private static bool ChunkMembersMatch(
            DynamicBuffer<ServerAniSelectionAssemblyMember> staged,
            AniSelectionChunkRpc rpc)
        {
            // 成员 Buffer 混合保存多个分块，需要按块号筛选再核对块内位置
            int found = 0;
            for (int index = 0; index < staged.Length; index++)
            {
                ServerAniSelectionAssemblyMember member = staged[index];
                if (member.ChunkIndex != rpc.ChunkIndex)
                {
                    // 其他分块成员与当前重传比较无关
                    continue;
                }

                // 长度越界或任意位置 GhostId 不同都代表冲突重传
                if (member.MemberIndex >= rpc.GhostIds.Length ||
                    member.GhostId != rpc.GhostIds[member.MemberIndex])
                {
                    return false;
                }

                found++;
            }

            // 内容相同但成员数量不同同样不能视为幂等重传
            return found == rpc.GhostIds.Length;
        }

        private NativeParallelHashMap<int, Entity> BuildSelectionOwnerMap()
        {
            // 每轮临时映射把后续按玩家查找从线性扫描降为常数时间
            using NativeArray<Entity> entities =
                _selectionQuery.ToEntityArray(Allocator.Temp);
            using NativeArray<ServerAniSelectionSet> selections =
                _selectionQuery.ToComponentDataArray<ServerAniSelectionSet>(Allocator.Temp);
            var result = new NativeParallelHashMap<int, Entity>(
                entities.Length + 16,
                Allocator.Temp);
            for (int index = 0; index < entities.Length; index++)
            {
                // 若数据异常出现同一玩家多个选择集，只保留第一个并等待诊断
                result.TryAdd(selections[index].OwnerNetworkId, entities[index]);
            }

            return result;
        }

        private NativeParallelHashMap<int, Entity> BuildAssemblyOwnerMap()
        {
            // 活动组装也按玩家建立临时映射，保证同一玩家只有一个版本推进
            using NativeArray<Entity> entities =
                _assemblyQuery.ToEntityArray(Allocator.Temp);
            using NativeArray<ServerAniSelectionAssembly> assemblies =
                _assemblyQuery.ToComponentDataArray<ServerAniSelectionAssembly>(Allocator.Temp);
            var result = new NativeParallelHashMap<int, Entity>(
                entities.Length + 16,
                Allocator.Temp);
            for (int index = 0; index < entities.Length; index++)
            {
                // TryAdd 不覆盖已有项，防止异常重复状态在本轮相互替换
                result.TryAdd(assemblies[index].OwnerNetworkId, entities[index]);
            }

            return result;
        }

        private static uint NextVersion(uint current)
        {
            // 零值用于未初始化状态，溢出后回到一继续计算超时距离
            uint next = current + 1;
            return next == 0 ? 1u : next;
        }
    }
}
