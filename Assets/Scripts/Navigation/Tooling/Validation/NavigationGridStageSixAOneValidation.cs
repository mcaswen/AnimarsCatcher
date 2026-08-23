#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using AnimarsCatcher.Gameplay;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    /// <summary>
    /// 验证 6A.1 万人选择集协议、权限边界和 MovementOrder 快照
    /// </summary>
    public static class NavigationGridStageSixAOneValidation
    {
        // 主测试连接拥有全部万人 Ani
        private const int OwnerNetworkId = 1;

        // 第二个编号用于构造不属于主测试连接的越权 Ani
        private const int OtherNetworkId = 2;

        // 验收规模直接使用阶段六承诺的正式上限
        private const int AgentCount = 10000;

        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage Six A One Validation")]
        private static void RunFromMenu()
        {
            // 菜单入口与命令行入口复用同一套断言，避免出现两种验收口径
            RunAll();
        }

        /// <summary>
        /// 供 Unity Batch Mode 执行 6A.1 自动验收
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAll();
        }

        /// <summary>
        /// 依次检查协议结构、万人乱序重放、异常拒绝和移动命令快照
        /// </summary>
        public static void RunAll()
        {
            // 先检查类型形状和 World 注册，尽早发现协议回退
            TestProtocolShapeAndSystemRegistration();

            // 逆序重放承担完整异常测试，模拟可靠 RPC 仍可能乱序到达的情况
            SelectionReplayResult reverse = RunSelectionReplay(reverseChunks: true, runFullChecks: true);

            // 顺序重放只提取结果，用来与逆序重放比较确定性
            SelectionReplayResult forward = RunSelectionReplay(reverseChunks: false, runFullChecks: false);

            // 两种到达顺序都必须完整保留一万名成员
            Assert(reverse.MemberCount == AgentCount, "逆序分块没有完整发布 10000 个成员");
            Assert(forward.MemberCount == AgentCount, "顺序分块没有完整发布 10000 个成员");

            // 最终 Hash 和 Buffer 顺序共同证明分块顺序没有泄露到业务结果
            Assert(reverse.SelectionHash == forward.SelectionHash, "分块到达顺序改变了选择集 Hash");
            Assert(reverse.MemberOrderHash == forward.MemberOrderHash, "分块到达顺序改变了成员顺序");

            // 日志只输出可以跨运行比对的规模、块数和确定性摘要
            Debug.Log(
                $"Navigation Grid 6A.1 自动验收通过：成员={AgentCount}，分块={reverse.ChunkCount}，" +
                $"SelectionHash={reverse.SelectionHash:X16}，MemberOrderHash={reverse.MemberOrderHash:X16}");
        }

        private static void TestProtocolShapeAndSystemRegistration()
        {
            // 反射直接检查公开字段，防止 AniCommandRpc 以后重新塞入固定列表
            FieldInfo[] commandFields = typeof(AniCommandRpc).GetFields(
                BindingFlags.Instance | BindingFlags.Public);

            // 版本和 Hash 必须同时存在，缺少任意一项都无法安全引用选择快照
            bool hasVersion = false;
            bool hasHash = false;
            for (int index = 0; index < commandFields.Length; index++)
            {
                // 使用字段名和精确类型共同约束网络契约
                FieldInfo field = commandFields[index];
                hasVersion |= field.Name == nameof(AniCommandRpc.SelectionVersion) &&
                              field.FieldType == typeof(uint);
                hasHash |= field.Name == nameof(AniCommandRpc.SelectionHash) &&
                           field.FieldType == typeof(ulong);

                // 检查所有 FixedList 规格，而不是只防止某一个容量重新出现
                Assert(
                    !field.FieldType.Name.StartsWith("FixedList", StringComparison.Ordinal),
                    "AniCommandRpc 不应继续携带固定容量成员列表");
            }

            // 移动命令必须同时携带服务器可核对的选择版本和内容摘要
            Assert(hasVersion && hasHash, "AniCommandRpc 缺少选择集版本或 Hash");

            // 协议常量乘积必须覆盖正式万人规模，最后一块允许不足额
            Assert(
                AniSelectionProtocol.MemberIdsPerChunk > 0 &&
                AniSelectionProtocol.MaximumChunkCount *
                AniSelectionProtocol.MemberIdsPerChunk >= AgentCount,
                "选择集分块容量不足以承载 10000 个成员");

            // 从 Unity 默认注册表读取真实 World 归属，而不是只检查特性是否存在
            IReadOnlyList<Type> serverSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ServerSimulation);
            IReadOnlyList<Type> clientSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ClientSimulation);

            // GhostId 索引必须在 Server World 自动注册，选择集才能解析成员
            Assert(
                ContainsSystem(serverSystems, typeof(ServerAniGhostIdIndexSystem)),
                "Server World 缺少增量 GhostId 索引 System");

            // 权威组装和权限校验也必须只依赖服务器自动启动
            Assert(
                ContainsSystem(serverSystems, typeof(ServerAniSelectionSetSystem)),
                "Server World 缺少权威选择集 System");

            // 客户端不能运行服务器索引和选择真值逻辑
            Assert(
                !ContainsSystem(clientSystems, typeof(ServerAniGhostIdIndexSystem)) &&
                !ContainsSystem(clientSystems, typeof(ServerAniSelectionSetSystem)),
                "服务器选择集 System 不能注册到 Client World");
        }

        private static SelectionReplayResult RunSelectionReplay(
            bool reverseChunks,
            bool runFullChecks)
        {
            // 每种分块顺序使用独立 World，防止 Entity 或版本状态相互污染
            using var world = new World(
                reverseChunks ? "Stage Six A One Reverse" : "Stage Six A One Forward",
                WorldFlags.Game);

            // 手工创建连接即可覆盖服务器协议，不需要启动 Transport
            EntityManager entityManager = world.EntityManager;
            Entity connection = entityManager.CreateEntity(typeof(NetworkId));
            entityManager.SetComponentData(connection, new NetworkId { Value = OwnerNetworkId });

            // 创建连续 GhostId 和稳定二维位置，便于逐项验证 Entity 映射
            Entity[] anis = CreateAnis(entityManager, AgentCount, OwnerNetworkId, 1);

            // 索引先运行一次，选择集处理时应直接复用该发布版本
            SystemHandle indexSystem = world.GetOrCreateSystem<ServerAniGhostIdIndexSystem>();
            indexSystem.Update(world.Unmanaged);

            // 初始索引必须覆盖全部万人且没有重复编号
            ValidateIndex(entityManager, AgentCount, 1, OwnerNetworkId);
            uint initialIndexVersion = GetIndexState(entityManager).Version;

            // 使用真实 SystemBase 实例处理后续模拟接收 RPC
            ServerAniSelectionSetSystem selectionSystem =
                world.GetOrCreateSystemManaged<ServerAniSelectionSetSystem>();

            // 完整选择采用一到一万的连续编号，期望结果与 payload 相同
            int[] allGhostIds = CreateSequentialIds(AgentCount, 1);

            // reverseChunks 决定创建 RPC Entity 的顺序，不改变任何协议元数据
            SendSelection(
                entityManager,
                connection,
                version: 1,
                AniSelectionUpdateMode.Replace,
                allGhostIds,
                allGhostIds,
                reverseChunks);

            // 一次更新应消费所有已经到达的 84 个分块并原子发布结果
            selectionSystem.Update();

            // 仅选择 RPC 变化不属于 Ghost 结构或所有权变化
            indexSystem.Update(world.Unmanaged);
            Assert(
                GetIndexState(entityManager).Version == initialIndexVersion,
                "只有选择 RPC 变化时不应重新发布 GhostId 索引");

            // 发布后每个玩家只能找到一个权威选择集 Entity
            Entity selectionEntity = GetSelectionEntity(entityManager, OwnerNetworkId);
            ServerAniSelectionSet selection =
                entityManager.GetComponentData<ServerAniSelectionSet>(selectionEntity);
            DynamicBuffer<ServerAniSelectionMember> members =
                entityManager.GetBuffer<ServerAniSelectionMember>(selectionEntity, true);

            // Component 计数、Buffer 长度和版本必须描述同一个完整快照
            Assert(selection.MemberCount == AgentCount, "万人选择集的成员计数错误");
            Assert(members.Length == AgentCount, "万人选择集 Buffer 长度错误");
            Assert(selection.Version == 1, "万人选择集版本错误");

            // 服务端发布顺序固定为 GhostId 升序，与分块到达顺序无关
            for (int index = 0; index < members.Length; index++)
            {
                Assert(members[index].GhostId == index + 1, "万人选择集成员没有按 GhostId 稳定排序");
            }

            // 从服务器实际 Buffer 重新计算 Hash，不能直接信任客户端声明值
            ulong orderHash = AniSelectionProtocol.ComputeSelectionHash(1, members);
            Assert(orderHash == selection.CompletenessHash, "发布选择集与完整性 Hash 不一致");

            // 返回摘要供另一个独立 World 做顺序无关性比较
            var replayResult = new SelectionReplayResult
            {
                MemberCount = members.Length,
                ChunkCount = CalculateChunkCount(allGhostIds.Length),
                SelectionHash = selection.CompletenessHash,
                MemberOrderHash = orderHash,
            };

            if (runFullChecks)
            {
                // 异常和命令测试只运行一次，控制万人验收的总耗时
                // 第一组验证选择快照能够完整进入正式移动命令
                TestMovementOrder(world, connection, selection, anis);
                // 第二组验证 Clear、Replace、Add 和 Remove 的连续版本切换
                TestCancelReplaceAndDelta(entityManager, connection, selectionSystem);
                // 第三组验证异常请求不会污染最后一次合法发布
                TestRejectedSelections(
                    entityManager,
                    connection,
                    selectionSystem,
                    indexSystem);
            }

            return replayResult;
        }

        private static void TestMovementOrder(
            World world,
            Entity connection,
            ServerAniSelectionSet selection,
            Entity[] anis)
        {
            // 命令入口依赖 Grid 后端标记，测试 World 必须显式配置
            EntityManager entityManager = world.EntityManager;
            AniMovementBackendWorldUtility.ConfigureWorld(
                world,
                AniMovementBackend.ClearanceGrid);

            // 直接构造 NetCode 接收后的 RPC Entity，覆盖真实服务器入口
            Entity rpcEntity = entityManager.CreateEntity(
                typeof(AniCommandRpc),
                typeof(ReceiveRpcCommandRequest));

            // 移动 RPC 只引用已经确认的版本和 Hash，不再携带成员列表
            entityManager.SetComponentData(rpcEntity, new AniCommandRpc
            {
                TargetKind = WorldCommandTargetKind.Ground,
                TargetWorldPosition = new float3(40f, 0f, 60f),
                TargetEntity = Entity.Null,
                SelectionVersion = selection.Version,
                SelectionHash = selection.CompletenessHash,
            });

            // 来源连接决定命令可访问哪一个权威选择集
            entityManager.SetComponentData(rpcEntity, new ReceiveRpcCommandRequest
            {
                SourceConnection = connection,
            });

            // 运行真实 Grid 命令入口，把 RPC 转换为 MovementOrder
            SystemHandle ingress = world.GetOrCreateSystem<ServerAniCommandIngressSystem>();
            ingress.Update(world.Unmanaged);

            // 查询同时要求订单头和成员 Buffer，避免只创建半个订单也通过
            using EntityQuery orders = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniMovementOrder>(),
                ComponentType.ReadOnly<AniMovementOrderMember>());

            // 一次移动 RPC 必须只生成一个高层订单
            Assert(orders.CalculateEntityCount() == 1, "万人选择集没有生成唯一 MovementOrder");

            // 读取订单头和冻结成员，逐项核对完整快照
            Entity orderEntity = orders.GetSingletonEntity();
            AniMovementOrder order = entityManager.GetComponentData<AniMovementOrder>(orderEntity);
            DynamicBuffer<AniMovementOrderMember> orderMembers =
                entityManager.GetBuffer<AniMovementOrderMember>(orderEntity, true);

            // 地面目标在订单层统一表达为 MoveTo
            Assert(order.Mode == AniSquadCommandMode.MoveTo, "地面命令没有转换为 MoveTo");

            // 版本和 Hash 必须原样绑定到创建订单时使用的选择集
            Assert(order.SelectionVersion == selection.Version, "MovementOrder 选择集版本错误");
            Assert(order.SelectionHash == selection.CompletenessHash, "MovementOrder 选择集 Hash 错误");
            Assert(order.CreatedTick != 0, "MovementOrder 没有记录服务器创建 Tick");
            Assert(order.CancellationVersion == order.Sequence,
                "MovementOrder 取消版本没有与命令序号对齐");
            // 成员数量使用正式万人上限，防止命令入口仍存在隐式截断
            Assert(orderMembers.Length == AgentCount, "MovementOrder 成员快照不完整");

            // GhostId 顺序和 Entity 对应关系都必须保持唯一且稳定
            for (int index = 0; index < orderMembers.Length; index++)
            {
                Assert(orderMembers[index].GhostId == index + 1, "MovementOrder 成员重复或顺序错误");
                Assert(orderMembers[index].Ani == anis[index], "MovementOrder 成员 Entity 映射错误");
                Assert(orderMembers[index].MaxSpeed == 5f &&
                       orderMembers[index].MaxAcceleration == 20f &&
                       orderMembers[index].AgentRadius > 0f &&
                       orderMembers[index].AgentProfile != 0,
                    "MovementOrder 没有冻结 Cohort 所需的移动配置");
            }

            using EntityQuery squadRequests = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniSquadCommandRequest>());
            Assert(squadRequests.IsEmptyIgnoreFilter,
                "正式 MovementOrder 不应继续生成兼容 Squad 请求");

            // 清除合法订单，确保下面的过期命令断言只观察新结果
            entityManager.DestroyEntity(orderEntity);

            // 过期版本不能借用当前选择集创建命令
            // 这里使用更高但未发布的版本，覆盖客户端先发命令后确认的竞态
            Entity staleRpc = entityManager.CreateEntity(
                typeof(AniCommandRpc),
                typeof(ReceiveRpcCommandRequest));

            // Hash 故意保持当前值，证明服务器会同时检查版本而不是只看 Hash
            entityManager.SetComponentData(staleRpc, new AniCommandRpc
            {
                TargetKind = WorldCommandTargetKind.Ground,
                TargetWorldPosition = new float3(10f, 0f, 10f),
                SelectionVersion = selection.Version + 1,
                SelectionHash = selection.CompletenessHash,
            });

            // 仍使用合法来源连接，失败原因应唯一落在选择版本不匹配
            entityManager.SetComponentData(staleRpc, new ReceiveRpcCommandRequest
            {
                SourceConnection = connection,
            });

            // 再次运行入口后不应出现任何 MovementOrder
            ingress.Update(world.Unmanaged);
            Assert(orders.IsEmptyIgnoreFilter, "错误选择集版本仍然生成了 MovementOrder");
        }

        private static void TestCancelReplaceAndDelta(
            EntityManager entityManager,
            Entity connection,
            ServerAniSelectionSetSystem selectionSystem)
        {
            // Clear 的 payload 和最终结果都必须为空
            int[] empty = Array.Empty<int>();

            // 版本二清除版本一发布的全部万人成员
            SendSelection(
                entityManager,
                connection,
                2,
                AniSelectionUpdateMode.Clear,
                empty,
                empty,
                reverseChunks: false);
            selectionSystem.Update();

            // Clear 复用同一个选择集 Entity，只替换内部成员快照
            Entity selectionEntity = GetSelectionEntity(entityManager, OwnerNetworkId);
            Assert(
                entityManager.GetBuffer<ServerAniSelectionMember>(selectionEntity).Length == 0,
                "清空选择没有移除原有万人成员");

            // Replace 使用非连续小集合，方便后续验证 Add 和 Remove 的归并顺序
            int[] replacement = { 1, 3, 5 };
            SendSelection(
                entityManager,
                connection,
                3,
                AniSelectionUpdateMode.Replace,
                replacement,
                replacement,
                reverseChunks: true);
            selectionSystem.Update();

            // 单块逆序与顺序相同，但仍复用统一发送帮助函数
            AssertSelection(entityManager, selectionEntity, 3, replacement);

            // Add 提交偶数成员，预期与旧奇数成员合并成连续集合
            int[] additions = { 2, 4 };
            int[] expanded = { 1, 2, 3, 4, 5 };
            SendSelection(
                entityManager,
                connection,
                4,
                AniSelectionUpdateMode.Add,
                additions,
                expanded,
                reverseChunks: false);
            selectionSystem.Update();

            // 合并结果必须去重并保持 GhostId 严格升序
            AssertSelection(entityManager, selectionEntity, 4, expanded);

            // Remove 使用与 Add 相同的 payload，预期准确恢复版本三内容
            SendSelection(
                entityManager,
                connection,
                5,
                AniSelectionUpdateMode.Remove,
                additions,
                replacement,
                reverseChunks: false);
            selectionSystem.Update();

            // 版本仍应递增，成员内容则与旧版本三相同
            AssertSelection(entityManager, selectionEntity, 5, replacement);
        }

        private static void TestRejectedSelections(
            EntityManager entityManager,
            Entity connection,
            ServerAniSelectionSetSystem selectionSystem,
            SystemHandle indexSystem)
        {
            // 所有拒绝案例共享版本五的稳定选择集，失败请求不得污染它
            Entity selectionEntity = GetSelectionEntity(entityManager, OwnerNetworkId);

            // 每个案例在操作前读取指标，断言对应拒绝计数确实增加
            ServerAniSelectionMetrics before = GetMetrics(entityManager);

            // 同一 GhostId 出现两次时，即使 Hash 自洽也不是合法选择集
            int[] duplicateMembers = { 1, 1 };
            SendSelection(
                entityManager,
                connection,
                6,
                AniSelectionUpdateMode.Replace,
                duplicateMembers,
                duplicateMembers,
                reverseChunks: false);
            selectionSystem.Update();

            // 被拒绝后仍应保留版本五和原有三个成员
            AssertSelection(entityManager, selectionEntity, 5, new[] { 1, 3, 5 });
            Assert(
                GetMetrics(entityManager).RejectedDuplicateCount > before.RejectedDuplicateCount,
                "重复成员没有被服务器拒绝");

            // 创建属于另一个玩家的 Ani，编号本身合法但选择权限非法
            Entity unauthorizedAni = CreateAni(
                entityManager,
                ghostId: AgentCount + 1,
                ownerNetworkId: OtherNetworkId);

            // 所有权和数量变化后索引必须刷新并包含新 Ani
            indexSystem.Update(entityManager.WorldUnmanaged);
            ValidateIndex(entityManager, AgentCount + 1, 1, OwnerNetworkId);
            ServerAniSelectionMetrics afterDuplicate = GetMetrics(entityManager);

            // 主连接尝试选择第二个玩家拥有的唯一成员
            int[] unauthorized = { AgentCount + 1 };
            SendSelection(
                entityManager,
                connection,
                7,
                AniSelectionUpdateMode.Replace,
                unauthorized,
                unauthorized,
                reverseChunks: false);
            selectionSystem.Update();

            // 权限拒绝必须进入专用指标，便于和格式错误区分
            Assert(
                GetMetrics(entityManager).RejectedUnauthorizedCount >
                afterDuplicate.RejectedUnauthorizedCount,
                "越权成员没有被服务器拒绝");

            // 越权请求不能覆盖最后一次合法选择版本
            AssertSelection(entityManager, selectionEntity, 5, new[] { 1, 3, 5 });

            // 重放与当前版本相同的 Replace，覆盖已发布版本拒绝路径
            ServerAniSelectionMetrics afterUnauthorized = GetMetrics(entityManager);
            SendSelection(
                entityManager,
                connection,
                5,
                AniSelectionUpdateMode.Replace,
                new[] { 1 },
                new[] { 1 },
                reverseChunks: false);
            selectionSystem.Update();

            // 相同版本按 stale 处理，不重复发布也不返回新的业务状态
            Assert(
                GetMetrics(entityManager).RejectedStaleCount > afterUnauthorized.RejectedStaleCount,
                "过期选择集版本没有被服务器拒绝");

            // 构造两个分块的版本，先只发送第零块建立活动组装
            int[] conflictPayload = CreateSequentialIds(
                AniSelectionProtocol.MemberIdsPerChunk + 1,
                1);
            int[] conflictResult = conflictPayload;

            // 首块使用正确 Hash 和合法成员
            AniSelectionChunkRpc first = CreateChunk(
                8,
                AniSelectionUpdateMode.Replace,
                conflictPayload,
                conflictResult,
                chunkIndex: 0);
            CreateReceivedRpc(entityManager, connection, first);
            selectionSystem.Update();

            // 复制同一位置后修改首成员并重算 Hash，形成内容冲突而非损坏包
            AniSelectionChunkRpc conflicting = first;
            conflicting.GhostIds[0] = 2;
            conflicting.ChunkHash = AniSelectionProtocol.ComputeChunkHash(
                conflicting.Version,
                conflicting.ChunkIndex,
                conflicting.ChunkCount,
                conflicting.GhostIds);

            // 冲突块仍来自合法连接，服务器必须按重复冲突拒绝整个版本
            CreateReceivedRpc(entityManager, connection, conflicting);
            ServerAniSelectionMetrics beforeConflict = GetMetrics(entityManager);
            selectionSystem.Update();

            // 指标增长证明不是把冲突块当成幂等网络重传
            Assert(
                GetMetrics(entityManager).RejectedDuplicateCount >
                beforeConflict.RejectedDuplicateCount,
                "内容冲突的重复分块没有被拒绝");

            // 再建立一个两块版本，但永远不发送最后一块
            int[] incompletePayload = CreateSequentialIds(
                AniSelectionProtocol.MemberIdsPerChunk + 1,
                1);
            AniSelectionChunkRpc incomplete = CreateChunk(
                9,
                AniSelectionUpdateMode.Replace,
                incompletePayload,
                incompletePayload,
                chunkIndex: 0);
            CreateReceivedRpc(entityManager, connection, incomplete);

            // 第一次更新创建并保留未完成组装 Entity
            selectionSystem.Update();
            ServerAniSelectionMetrics beforeTimeout = GetMetrics(entityManager);

            // 推进超过协议超时预算的一次更新，覆盖边界值和下一 Tick
            for (uint tick = 0; tick <= AniSelectionProtocol.AssemblyTimeoutTicks; tick++)
            {
                selectionSystem.Update();
            }

            // 超时必须计入未完成指标，并且不能发布部分成员
            Assert(
                GetMetrics(entityManager).RejectedIncompleteCount >
                beforeTimeout.RejectedIncompleteCount,
                "缺块选择集没有在超时后清理");

            // 查询临时组装类型，确认没有残留 Buffer 占用
            using EntityQuery assemblies = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ServerAniSelectionAssembly>());
            Assert(assemblies.IsEmptyIgnoreFilter, "超时后仍残留未完成选择集 Entity");

            // 删除越权测试 Ani，验证索引也能响应成员销毁
            entityManager.DestroyEntity(unauthorizedAni);
            indexSystem.Update(entityManager.WorldUnmanaged);

            // 索引应恢复原万人规模和原始起始编号
            ValidateIndex(entityManager, AgentCount, 1, OwnerNetworkId);
        }

        private static Entity[] CreateAnis(
            EntityManager entityManager,
            int count,
            int ownerNetworkId,
            int firstGhostId)
        {
            // Archetype 包含索引、选择和命令入口实际依赖的全部组件
            EntityArchetype archetype = entityManager.CreateArchetype(
                typeof(AniAttributes),
                typeof(GhostInstance),
                typeof(GhostOwner),
                typeof(LocalTransform),
                typeof(AniSelectedTag),
                typeof(PickerAniTag));

            // 批量创建避免万人验收把时间浪费在逐 Entity 结构变更上
            using NativeArray<Entity> created =
                entityManager.CreateEntity(archetype, count, Allocator.Temp);

            // 托管数组只用于之后核对 MovementOrder 的 Entity 映射
            var result = new Entity[count];
            for (int index = 0; index < count; index++)
            {
                // 创建顺序与连续 GhostId 顺序一致，便于发现任何错位
                Entity ani = created[index];
                // 托管数组保持相同下标，供订单成员逐项核对
                result[index] = ani;
                entityManager.SetComponentData(ani, new GhostInstance
                {
                    ghostId = firstGhostId + index,
                });

                // GhostOwner 是选择权限的服务器事实来源
                entityManager.SetComponentData(ani, new GhostOwner
                {
                    NetworkId = ownerNetworkId,
                });

                // AniAttributes 同时让 Entity 满足正式 Ani 查询
                entityManager.SetComponentData(ani, new AniAttributes
                {
                    MovementSpeed = 5f,
                    OwnerPlayerId = ownerNetworkId,
                });

                // 一百列网格提供非重叠且可预测的位置分布
                entityManager.SetComponentData(ani, LocalTransform.FromPosition(
                    new float3(index % 100, 0f, index / 100)));

                // 初始选择标记关闭，发布流程必须显式启用它
                entityManager.SetComponentEnabled<AniSelectedTag>(ani, false);
            }

            return result;
        }

        private static Entity CreateAni(
            EntityManager entityManager,
            int ghostId,
            int ownerNetworkId)
        {
            // 单体创建用于在索引发布后注入所有权不同的新 Ani
            Entity ani = entityManager.CreateEntity(
                typeof(AniAttributes),
                typeof(GhostInstance),
                typeof(GhostOwner),
                typeof(LocalTransform),
                typeof(AniSelectedTag),
                typeof(PickerAniTag));

            // GhostId 与 Owner 分开写入，索引刷新必须同时观察两者
            entityManager.SetComponentData(ani, new GhostInstance { ghostId = ghostId });
            entityManager.SetComponentData(ani, new GhostOwner { NetworkId = ownerNetworkId });
            // 属性所有者与 GhostOwner 保持一致，只有网络所有权不同于主玩家
            entityManager.SetComponentData(ani, new AniAttributes
            {
                MovementSpeed = 5f,
                OwnerPlayerId = ownerNetworkId,
            });
            entityManager.SetComponentData(ani, LocalTransform.FromPosition(float3.zero));

            // 测试 Ani 不应因为创建而自动进入当前选择集
            entityManager.SetComponentEnabled<AniSelectedTag>(ani, false);
            return ani;
        }

        private static void SendSelection(
            EntityManager entityManager,
            Entity connection,
            uint version,
            AniSelectionUpdateMode mode,
            int[] payload,
            int[] result,
            bool reverseChunks)
        {
            // 块数从 payload 推导，result 只描述更新后的最终集合
            int chunkCount = CalculateChunkCount(payload.Length);
            for (int offset = 0; offset < chunkCount; offset++)
            {
                // 逆序模式只改变创建次序，块自身仍携带原始 ChunkIndex
                int chunkIndex = reverseChunks ? chunkCount - offset - 1 : offset;

                // 每个构造出的 RPC 都模拟已由 NetCode 接收并附加来源连接
                CreateReceivedRpc(
                    entityManager,
                    connection,
                    CreateChunk(version, mode, payload, result, chunkIndex));
            }
        }

        private static AniSelectionChunkRpc CreateChunk(
            uint version,
            AniSelectionUpdateMode mode,
            int[] payload,
            int[] result,
            int chunkIndex)
        {
            // 空 payload 也会得到一个空块，匹配 Clear 的协议表达
            int chunkCount = CalculateChunkCount(payload.Length);

            // FixedList 只复制当前分块范围，不承载完整选择集
            FixedList512Bytes<int> ghostIds = default;

            // start 和 end 使用协议常量，确保测试不会另设分块规则
            int start = chunkIndex * AniSelectionProtocol.MemberIdsPerChunk;
            int end = math.min(start + AniSelectionProtocol.MemberIdsPerChunk, payload.Length);
            // end 使用开区间，恰好覆盖当前分块且不会越过 payload 尾部
            for (int index = start; index < end; index++)
            {
                ghostIds.Add(payload[index]);
            }

            // 每块重复携带公共结果元数据，服务器可在未收齐前发现冲突
            var rpc = new AniSelectionChunkRpc
            {
                Version = version,
                Mode = mode,
                ChunkIndex = (ushort)chunkIndex,
                ChunkCount = (ushort)chunkCount,
                PayloadMemberCount = payload.Length,
                ResultMemberCount = result.Length,
                ResultHash = ComputeHash(version, result),
                GhostIds = ghostIds,
            };

            // 块 Hash 必须在 GhostIds 完整写入后计算
            rpc.ChunkHash = AniSelectionProtocol.ComputeChunkHash(
                rpc.Version,
                rpc.ChunkIndex,
                rpc.ChunkCount,
                rpc.GhostIds);
            // 返回值已经包含服务器包络校验需要的全部字段
            return rpc;
        }

        private static void CreateReceivedRpc(
            EntityManager entityManager,
            Entity connection,
            AniSelectionChunkRpc rpc)
        {
            // 使用正式 RPC Component 与 ReceiveRpcCommandRequest 组合模拟网络落地结果
            Entity rpcEntity = entityManager.CreateEntity(
                typeof(AniSelectionChunkRpc),
                typeof(ReceiveRpcCommandRequest));

            // 内容和来源分开写入，保持与 NetCode 创建的接收 Entity 形状一致
            entityManager.SetComponentData(rpcEntity, rpc);
            // SourceConnection 是服务器权限判断唯一可信的玩家入口
            entityManager.SetComponentData(rpcEntity, new ReceiveRpcCommandRequest
            {
                SourceConnection = connection,
            });
        }

        private static void AssertSelection(
            EntityManager entityManager,
            Entity selectionEntity,
            uint version,
            int[] expected)
        {
            // 同时读取选择头和成员 Buffer，避免只检查单侧状态
            ServerAniSelectionSet selection =
                entityManager.GetComponentData<ServerAniSelectionSet>(selectionEntity);
            DynamicBuffer<ServerAniSelectionMember> members =
                entityManager.GetBuffer<ServerAniSelectionMember>(selectionEntity, true);

            // 版本、两个计数和完整性 Hash 必须全部与期望结果一致
            Assert(selection.Version == version, $"选择集版本应为 {version}，实际为 {selection.Version}");
            // Buffer 长度验证实际数据，Component 计数验证发布元数据
            Assert(members.Length == expected.Length, "选择集成员数量与预期不一致");
            Assert(selection.MemberCount == expected.Length, "选择集组件成员计数与 Buffer 不一致");
            // 使用当前期望版本计算 Hash，防止相同成员跨版本误匹配
            Assert(selection.CompletenessHash == ComputeHash(version, expected), "选择集完整性 Hash 错误");

            // 按下标核对成员，同时验证内容和稳定排序
            for (int index = 0; index < expected.Length; index++)
            {
                // expected 本身按升序提供，因此逐项相等也验证了稳定顺序
                Assert(members[index].GhostId == expected[index], "选择集成员内容或顺序错误");
            }
        }

        private static void ValidateIndex(
            EntityManager entityManager,
            int expectedCount,
            int firstGhostId,
            int expectedFirstOwner)
        {
            // 索引头和索引项必须由同一个单例 Entity 发布
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ServerAniGhostIdIndex>(),
                ComponentType.ReadOnly<ServerAniGhostIdIndexEntry>());
            Entity indexEntity = query.GetSingletonEntity();
            ServerAniGhostIdIndex index =
                entityManager.GetComponentData<ServerAniGhostIdIndex>(indexEntity);
            DynamicBuffer<ServerAniGhostIdIndexEntry> entries =
                entityManager.GetBuffer<ServerAniGhostIdIndexEntry>(indexEntity, true);

            // Component 计数与 Buffer 长度分别检查，防止元数据未同步
            Assert(
                index.EntryCount == expectedCount,
                $"GhostId 索引计数错误：预期 {expectedCount}，实际 {index.EntryCount}，重复 {index.DuplicateGhostIdCount}");
            Assert(
                entries.Length == expectedCount,
                $"GhostId 索引 Buffer 长度错误：预期 {expectedCount}，实际 {entries.Length}");

            // 起始编号和首项所有权证明排序及权限信息都被发布
            Assert(entries[0].GhostId == firstGhostId, "GhostId 索引起始编号错误");
            Assert(entries[0].OwnerNetworkId == expectedFirstOwner, "GhostId 索引拥有者错误");

            // 严格小于同时排除乱序和重复 GhostId
            for (int entryIndex = 1; entryIndex < entries.Length; entryIndex++)
            {
                // 万项全扫描同时覆盖中间项和数组尾部
                Assert(
                    entries[entryIndex - 1].GhostId < entries[entryIndex].GhostId,
                    "GhostId 索引没有保持严格升序");
            }
        }

        private static Entity GetSelectionEntity(
            EntityManager entityManager,
            int ownerNetworkId)
        {
            // 测试辅助查询按 OwnerNetworkId 定位玩家唯一选择集
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ServerAniSelectionSet>());
            using NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            for (int index = 0; index < entities.Length; index++)
            {
                // 不依赖查询顺序，显式比较业务所有者编号
                if (entityManager.GetComponentData<ServerAniSelectionSet>(entities[index])
                        .OwnerNetworkId == ownerNetworkId)
                {
                    // 找到匹配玩家后立即返回，正常状态不应存在第二个结果
                    return entities[index];
                }
            }

            // 找不到选择集代表发布流程没有完成，直接终止验收
            throw new InvalidOperationException($"未找到玩家 {ownerNetworkId} 的服务器选择集");
        }

        private static ServerAniSelectionMetrics GetMetrics(EntityManager entityManager)
        {
            // 指标是 Server World 单例，拒绝场景通过前后差值判断命中分支
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ServerAniSelectionMetrics>());
            // 查询必须保持单例，否则 GetSingleton 会直接暴露生命周期错误
            return query.GetSingleton<ServerAniSelectionMetrics>();
        }

        private static ServerAniGhostIdIndex GetIndexState(EntityManager entityManager)
        {
            // 只读取索引头即可比较发布版本，不复制万项 Buffer
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ServerAniGhostIdIndex>());
            // 不复制成员 Buffer，版本断言只关心索引是否重新发布
            return query.GetSingleton<ServerAniGhostIdIndex>();
        }

        private static ulong ComputeHash(uint version, int[] sortedIds)
        {
            // 托管期望数组临时转换为 NativeArray，复用正式协议算法
            using var ids = new NativeArray<int>(sortedIds, Allocator.Temp);
            // 临时数组在方法返回前释放，验收不会积累万人级 Native 内存
            return AniSelectionProtocol.ComputeSelectionHash(version, ids);
        }

        private static int CalculateChunkCount(int memberCount)
        {
            // 至少返回一块，使空选择也能通过 RPC 表达
            return math.max(
                1,
                // 整数向上取整与正式客户端发送逻辑保持相同
                (memberCount + AniSelectionProtocol.MemberIdsPerChunk - 1) /
                AniSelectionProtocol.MemberIdsPerChunk);
        }

        private static int[] CreateSequentialIds(int count, int firstId)
        {
            // 连续编号让期望顺序无需额外排序即可直接比较
            var result = new int[count];
            for (int index = 0; index < count; index++)
            {
                // firstId 允许同一帮助函数构造任意连续编号区间
                result[index] = firstId + index;
            }

            return result;
        }

        private static bool ContainsSystem(IReadOnlyList<Type> systems, Type targetType)
        {
            // 默认 System 注册表规模很小，线性扫描足以表达精确类型检查
            for (int index = 0; index < systems.Count; index++)
            {
                if (systems[index] == targetType)
                {
                    // 精确类型相等避免把名称相似的 System 当成正确注册
                    return true;
                }
            }

            // 扫描结束仍未命中时说明目标 System 没有注册到该 World
            return false;
        }

        private static void Assert(bool condition, string message)
        {
            // 使用异常让菜单运行和 Batch Mode 都得到明确失败结果
            if (!condition)
            {
                // 保留调用点给出的中文原因，Batch Mode 日志可以直接定位失败
                throw new InvalidOperationException(message);
            }
        }

        private struct SelectionReplayResult
        {
            // 权威选择集实际发布的成员数量
            public int MemberCount;

            // 本次万人提交使用的协议分块数
            public int ChunkCount;

            // 选择集 Component 保存的完整性 Hash
            public ulong SelectionHash;

            // 从实际成员 Buffer 重新计算的顺序 Hash
            public ulong MemberOrderHash;
        }
    }
}
#endif
