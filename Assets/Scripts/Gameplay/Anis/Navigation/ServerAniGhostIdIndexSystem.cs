using System.Collections.Generic;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在服务器按 Ani 结构变化增量发布稳定的 GhostId 索引
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct ServerAniGhostIdIndexSystem : ISystem
    {
        // 提供生成索引所需的稳定 Ani、GhostId 和所有权快照
        private EntityQuery _aniQuery;

        // 专门承载 ChangedVersionFilter，避免改变主查询的普通读取语义
        private EntityQuery _changedAniQuery;

        // 索引使用独立单例 Entity 发布，供选择和命令入口共享
        private Entity _indexEntity;

        // 数量变化可以覆盖新增和销毁 Ani 的常见情况
        private int _lastEntityCount;

        // Archetype 或 Chunk 结构变化时强制刷新 Entity 映射
        private int _lastOrderVersion;

        public void OnCreate(ref SystemState state)
        {
            // 主查询不设置变更过滤，重建时必须读取全部现存 Ani
            _aniQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<AniAttributes>(),
                ComponentType.ReadOnly<GhostInstance>(),
                ComponentType.ReadOnly<GhostOwner>());

            // 额外加入稳定存在的 LocalTransform，使两个查询不会复用同一个内部查询对象
            _changedAniQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<AniAttributes>(),
                ComponentType.ReadOnly<GhostInstance>(),
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadOnly<LocalTransform>());

            // GhostId 变化会改变网络编号到 Entity 的解析结果
            _changedAniQuery.AddChangedVersionFilter(
                ComponentType.ReadOnly<GhostInstance>());

            // GhostOwner 变化会改变选择权限，必须与编号变化同等处理
            _changedAniQuery.AddChangedVersionFilter(
                ComponentType.ReadOnly<GhostOwner>());

            // Buffer 初始为空，第一次更新通过哨兵值触发完整发布
            _indexEntity = state.EntityManager.CreateEntity();
            state.EntityManager.AddComponentData(_indexEntity, new ServerAniGhostIdIndex());
            state.EntityManager.AddBuffer<ServerAniGhostIdIndexEntry>(_indexEntity);
            _lastEntityCount = -1;
            _lastOrderVersion = int.MinValue;
        }

        public void OnUpdate(ref SystemState state)
        {
            // 结构版本、数量和关键组件都未变化时复用上一版索引
            int orderVersion = _aniQuery.GetCombinedComponentOrderVersion(false);
            int entityCount = _aniQuery.CalculateEntityCount();
            if (orderVersion == _lastOrderVersion &&
                entityCount == _lastEntityCount &&
                _changedAniQuery.IsEmpty)
            {
                return;
            }

            // 在读取数据前记录触发条件，下一次更新只检查新的变化
            _lastOrderVersion = orderVersion;
            _lastEntityCount = entityCount;

            // 三组数组来自同一查询顺序，相同下标共同描述一个 Ani
            using NativeArray<Entity> entities = _aniQuery.ToEntityArray(Allocator.Temp);
            using NativeArray<GhostInstance> ghosts =
                _aniQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
            using NativeArray<GhostOwner> owners =
                _aniQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            var entries =
                new NativeArray<ServerAniGhostIdIndexEntry>(entities.Length, Allocator.Temp);

            // 先生成连续临时快照，避免排序过程中反复访问 Chunk 数据
            for (int index = 0; index < entities.Length; index++)
            {
                entries[index] = new ServerAniGhostIdIndexEntry
                {
                    GhostId = ghosts[index].ghostId,
                    Ani = entities[index],
                    OwnerNetworkId = owners[index].NetworkId,
                };
            }

            // 排序让后续权限校验可以使用无分配二分查找
            entries.Sort(new GhostIdIndexEntryComparer());

            // 索引按版本整体替换，读取方不会观察到半更新状态
            DynamicBuffer<ServerAniGhostIdIndexEntry> published =
                state.EntityManager.GetBuffer<ServerAniGhostIdIndexEntry>(_indexEntity);
            published.Clear();
            published.EnsureCapacity(entries.Length);

            // 冲突编号无法唯一定位 Ani，因此整组都不发布
            int duplicateCount = 0;
            for (int index = 0; index < entries.Length;)
            {
                // 已排序数组中的同号项一定连续，可以一次扫描完成分组
                int groupEnd = index + 1;
                while (groupEnd < entries.Length &&
                       entries[groupEnd].GhostId == entries[index].GhostId)
                {
                    groupEnd++;
                }

                if (groupEnd - index == 1)
                {
                    // 只有唯一 GhostId 才能进入权威解析索引
                    published.Add(entries[index]);
                }
                else
                {
                    // 指标记录被排除的 Ani 数量，而不是冲突组数量
                    duplicateCount += groupEnd - index;
                }

                index = groupEnd;
            }

            // 版本在 Buffer 完整替换后递增，表示新快照已经可读
            ServerAniGhostIdIndex indexState =
                state.EntityManager.GetComponentData<ServerAniGhostIdIndex>(_indexEntity);
            indexState.Version = NextVersion(indexState.Version);
            indexState.EntryCount = published.Length;
            indexState.DuplicateGhostIdCount = duplicateCount;
            state.EntityManager.SetComponentData(_indexEntity, indexState);
            entries.Dispose();
        }

        /// <summary>
        /// 在已排序索引中查找 GhostId
        /// </summary>
        public static bool TryResolve(
            DynamicBuffer<ServerAniGhostIdIndexEntry> entries,
            int ghostId,
            out ServerAniGhostIdIndexEntry result)
        {
            // Buffer 始终严格按 GhostId 升序，因此查找复杂度为 O(log N)
            int low = 0;
            int high = entries.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                ServerAniGhostIdIndexEntry candidate = entries[middle];
                if (candidate.GhostId == ghostId)
                {
                    // 返回项同时包含 Entity 和 Owner，调用方无需再次建立映射
                    result = candidate;
                    return true;
                }

                if (candidate.GhostId < ghostId)
                {
                    // 目标只可能位于中点右侧
                    low = middle + 1;
                }
                else
                {
                    // 目标只可能位于中点左侧
                    high = middle - 1;
                }
            }

            result = default;
            return false;
        }

        /// <summary>
        /// 在索引快照中查找 GhostId，允许调用方安全执行结构变更
        /// </summary>
        public static bool TryResolve(
            NativeArray<ServerAniGhostIdIndexEntry> entries,
            int ghostId,
            out ServerAniGhostIdIndexEntry result)
        {
            // NativeArray 重载供即将执行结构变更的调用方读取稳定快照
            int low = 0;
            int high = entries.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                ServerAniGhostIdIndexEntry candidate = entries[middle];
                if (candidate.GhostId == ghostId)
                {
                    // 快照中的 Entity 仍需由调用方按业务要求复核存活状态
                    result = candidate;
                    return true;
                }

                if (candidate.GhostId < ghostId)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            result = default;
            return false;
        }

        private static uint NextVersion(uint current)
        {
            // 零值保留给尚未发布状态，溢出时从一重新开始
            uint next = current + 1;
            return next == 0 ? 1u : next;
        }

        private struct GhostIdIndexEntryComparer : IComparer<ServerAniGhostIdIndexEntry>
        {
            public int Compare(
                ServerAniGhostIdIndexEntry left,
                ServerAniGhostIdIndexEntry right)
            {
                // GhostId 是索引主键，也是后续二分查找使用的排序依据
                int ghostCompare = left.GhostId.CompareTo(right.GhostId);
                if (ghostCompare != 0)
                {
                    return ghostCompare;
                }

                // 冲突编号使用 Entity 标识提供确定性次序，方便整组排除
                int indexCompare = left.Ani.Index.CompareTo(right.Ani.Index);
                return indexCompare != 0
                    ? indexCompare
                    : left.Ani.Version.CompareTo(right.Ani.Version);
            }
        }
    }
}
