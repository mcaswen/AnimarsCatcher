using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Assertions;

namespace Unity.NetCode
{
    /// <summary>
    /// 基于距离缩放 Importance 时使用的实体级索引信息
    /// </summary>
    public struct GhostDistancePartitionShared : ISharedComponentData
    {
        /// <summary>
        /// 确定实体所属的 Tile 索引
        /// </summary>
        public int3 Index;
    }

    /// <summary>
    /// 每个 Tick 自动为服务器上的每个 Ghost 实例添加 <see cref="GhostDistancePartitionShared"/> Shared Component
    /// 此操作会产生结构变更，之后如果某个 Ghost 实例的 <see cref="LocalTransform.Position"/> 移动到新的 Tile
    /// 则更新该组件，由于需要修改 Shared Component 值，这同样会产生结构变更
    /// </summary>
    /// <remarks>
    ///     <para>
    /// 此系统仅在 ServerWorld 中检测到 <see cref="GhostDistanceData"/> 配置单例组件时运行
    ///     </para>
    ///     <para>
    /// 注意，为每个 Ghost 实例添加 <see cref="GhostDistancePartitionShared"/> Shared Component
    /// 几乎必然会加剧 <see cref="ArchetypeChunk"/> 的实体碎片化，因为系统利用 <see cref="ArchetypeChunk"/>
    /// 对 Ghost 实例进行空间分区，例如某个 Tile 内只有两个相同 Archetype 的 Ghost 时
    /// 它们所在 Chunk 最多只会包含两个实体
    ///     </para>
    ///     <para>
    /// 启用 Importance 缩放前应测量其影响并与其他可选方案进行基准比较
    /// 成本来源包括系统需要检查的位置变化数量、系统产生结构变更的频率
    /// 以及 Shared Component 本身造成的碎片化
    ///     </para>
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    // 在绝大多数系统之前更新，确保 Command Buffer 中没有等待执行的 DestroyEntity
    [UpdateInGroup(typeof(GhostSimulationSystemGroup), OrderFirst = true)]
    [BurstCompile]
    public partial struct GhostDistancePartitioningSystem : ISystem, ISystemStartStop
    {
        /// <summary>
        /// 为 true 时，此系统会为所有满足 <see cref="LocalTransform"/> 等筛选条件的服务器 Ghost 实例
        /// 添加 <see cref="GhostDistancePartitionShared"/> Shared Component，默认值为 true
        /// </summary>
        /// <remarks>
        /// 如果希望将 Shared Component 用作筛选器，仅对部分 Ghost 实例启用 Importance 缩放，则设为 false
        /// 在这种情况下必须自行添加该组件
        /// </remarks>
        public static bool AutomaticallyAddGhostDistancePartitionSharedComponent
        {
            get => s_AutomaticallyAddGhostDistancePartitionShared.Data;
            set => s_AutomaticallyAddGhostDistancePartitionShared.Data = value;
        }
        private static readonly SharedStatic<bool> s_AutomaticallyAddGhostDistancePartitionShared = SharedStatic<bool>.GetOrCreate<GhostDistancePartitioningSystem>();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            AutomaticallyAddGhostDistancePartitionSharedComponent = true;
        }

        EntityQuery m_DistancePartitionedEntitiesQuery;
        EntityTypeHandle m_EntityTypeHandle;
        ComponentTypeHandle<LocalTransform> m_Transform;
        SharedComponentTypeHandle<GhostDistancePartitionShared> m_SharedPartition;

        [BurstCompile]
        [WithChangeFilter(typeof(LocalTransform), typeof(GhostDistancePartitionShared))]
        // WithChangeFilter 优化：如果 Chunk 内没有实体移动，则无需重新计算其中每个实体的 Tile 索引
        struct UpdateTileIndexJob : IJobChunk
        {
            [ReadOnly] public SharedComponentTypeHandle<GhostDistancePartitionShared> TileTypeHandle;
            [ReadOnly] public ComponentTypeHandle<LocalTransform> TransHandle;
            [ReadOnly] public EntityTypeHandle EntityTypeHandle;
            public GhostDistanceData Config;
            public EntityCommandBuffer.ParallelWriter Ecb;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                var tile = chunk.GetSharedComponent(TileTypeHandle);
                var transforms = chunk.GetNativeArray(ref TransHandle);
                var entities = chunk.GetNativeArray(EntityTypeHandle);

                for (var index = 0; index < transforms.Length; index++)
                {
                    var transform = transforms[index];
                    var origTilePos = tile.Index * Config.TileSize + Config.TileCenter;
                    if (math.all(transform.Position >= origTilePos - Config.TileBorderWidth) &&
                        math.all(transform.Position <= origTilePos + Config.TileSize + Config.TileBorderWidth))
                    {
                        continue;
                    }

                    var tileIndex = CalculateTile(in Config, transform.Position);
                    if (math.all(tile.Index == tileIndex))
                    {
                        continue;
                    }

                    var entity = entities[index];
                    Ecb.SetSharedComponent(unfilteredChunkIndex, entity, new GhostDistancePartitionShared { Index = tileIndex });
                }
            }
        }

        [BurstCompile]
        [WithAll(typeof(GhostInstance))]
        [WithAbsent(typeof(GhostDistancePartitionShared))]
        partial struct AddSharedDistancePartitionJob : IJobEntity
        {
            public GhostDistanceData Config;
            public EntityCommandBuffer.ParallelWriter ConcurrentCommandBuffer;

            void Execute(Entity ent, [ChunkIndexInQuery]int chunkIndexInQuery, in LocalTransform trans)
            {
                var tileIndex = CalculateTile(Config, trans.Position);
                ConcurrentCommandBuffer.AddSharedComponent(chunkIndexInQuery, ent, new GhostDistancePartitionShared{Index = tileIndex});
            }
        }

        /// <summary>
        /// 计算指定位置对应的 Tile 值
        /// </summary>
        /// <param name="ghostDistanceData">Ghost 距离数据</param>
        /// <param name="position">位置</param>
        /// <returns>指定位置对应的 Tile 值</returns>
        public static int3 CalculateTile(in GhostDistanceData ghostDistanceData, in float3 position)
        {
            return ((int3) position - ghostDistanceData.TileCenter) / ghostDistanceData.TileSize;
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<GhostDistanceData>();
#if ENABLE_UNITY_COLLECTIONS_CHECKS || NETCODE_DEBUG
            // 验证 DistanceData 包含有效的范围和值
            if (config.TileSize.Equals(int3.zero))
            {
                var netDebug = SystemAPI.GetSingleton<NetDebug>();
                netDebug.LogError("GhostDistanceData.TileSize must always be different than int3.zero. You must specify a non zero tile size for at least one of the axis.");
                return;
            }
            if (config.TileSize.x < 0 || config.TileSize.y < 0 || config.TileSize.z < 0)
            {
                var netDebug = SystemAPI.GetSingleton<NetDebug>();
                netDebug.LogError($"Invalid GhostDistanceData.TileSize ({config.TileSize}) set for GhostDistanceData singleton.\nThe tile size for each individual axis must be a value greater than or equals zero");
                return;
            }
#endif
            var barrier = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>();
            if (AutomaticallyAddGhostDistancePartitionSharedComponent)
            {
                state.Dependency = new AddSharedDistancePartitionJob
                {
                    ConcurrentCommandBuffer = barrier.CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),
                    Config = config,
                }.Schedule(state.Dependency);
                // 在单个 Tick 生成数百个新 Ghost 时，此处使用 ScheduleParallel 可将实际耗时降低一半以上
                // 但调度开销会使平均耗时增加约 7%，UpdateTileIndexJob 耗时也会变差
                // 因此通常不值得并行调度
            }

            m_EntityTypeHandle.Update(ref state);
            m_Transform.Update(ref state);
            m_SharedPartition.Update(ref state);

            state.Dependency = new UpdateTileIndexJob
            {
                Config = config,
                Ecb = barrier.CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),
                EntityTypeHandle = m_EntityTypeHandle,
                TileTypeHandle = m_SharedPartition,
                TransHandle = m_Transform,
            }.ScheduleParallel(m_DistancePartitionedEntitiesQuery, state.Dependency);
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_Transform = state.GetComponentTypeHandle<LocalTransform>(true);
            m_SharedPartition = state.GetSharedComponentTypeHandle<GhostDistancePartitionShared>();
            state.RequireForUpdate<GhostImportance>();
            state.RequireForUpdate<GhostDistanceData>();
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GhostDistancePartitionShared, LocalTransform, GhostInstance>();
            m_DistancePartitionedEntitiesQuery = state.GetEntityQuery(builder);
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnStartRunning(ref SystemState state)
        {
        }

        /// <summary>
        /// 清理系统添加的所有 GhostDistancePartitionShared 组件
        /// 注意，此操作不会自动整理已经碎片化的 Chunk
        /// </summary>
        /// <inheritdoc/>
        [BurstCompile]
        public void OnStopRunning(ref SystemState state)
        {
            state.EntityManager.RemoveComponent<GhostDistancePartitionShared>(m_DistancePartitionedEntitiesQuery);
        }
    }
}
