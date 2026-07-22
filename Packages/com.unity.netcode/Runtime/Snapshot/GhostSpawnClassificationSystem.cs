using System;
using Unity.Entities;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.NetCode.LowLevel;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于升级到新组件类型的临时类型，将在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("GhostSpawnQueueComponent has been deprecated. Use GhostSpawnQueueComponent instead (UnityUpgradable) -> GhostSpawnQueue", true)]
    public struct GhostSpawnQueueComponent : IComponentData
    {}

    /// <summary>
    /// GhostSpawnQueue 用于标识包含 GhostSpawnBuffer 的单例组件
    /// </summary>
    public struct GhostSpawnQueue : IComponentData
    {

    }

    /// <summary>
    /// GhostSpawnBuffer 是 GhostSpawnQueue 单例的数据，包含将在下一帧开始时由 GhostSpawnSystem 生成的 Ghost 列表
    /// GhostReceiveSystem 负责填充此 Buffer
    /// 还需要一个在 GhostReceiveSystem 之后更新的分类系统设置 SpawnType
    /// 使生成系统知道应如何生成 Ghost
    /// 分类系统只应修改此结构的 SpawnType 和 PredictedSpawnEntity 字段
    /// InternalBufferCapacity 的配置接近填满 Chunk 内存
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct GhostSpawnBuffer : IBufferElementData
    {
        /// <summary>
        /// 生成实体时使用的 Ghost 模式
        /// </summary>
        public enum Type
        {
            /// <summary>
            /// Ghost 尚未分类，预期分类系统会将此值改为正确的 Ghost 模式
            /// 另见 <see cref="GhostSpawnClassificationSystem"/>
            /// </summary>
            Unknown,
            /// <summary>
            /// 新 Ghost 必须以插值模式生成
            /// 创建过程会延迟到 <see cref="NetworkTime.InterpolationTick"/> 等于或大于服务器实际生成 Tick
            /// 参见 <see cref="GhostSpawnSystem"/> 和 <see cref="PendingSpawnPlaceholder"/>
            /// </summary>
            Interpolated,
            /// <summary>
            /// 此 Ghost 为预测 Ghost，通常会立即创建新的 Ghost 实例
            /// 但如果 <see cref="PredictedSpawnEntity"/> 已设为有效实体引用
            /// 则改用该实体作为复制收到 Ghost Snapshot 的目标
            /// </summary>
            Predicted
        }
        /// <summary>
        /// 要生成的 Ghost 类型
        /// 根据生成类型，实例化 Ghost 的部分组件可能被启用、禁用或移除
        /// </summary>
        public Type SpawnType;
        /// <summary>
        /// Ghost 类型在 <see cref="GhostCollectionPrefab"/> 集合中的索引
        /// 由 <see cref="GhostSpawnClassificationSystem"/> 用于分类 Ghost
        /// </summary>
        public int GhostType;
        /// <summary>
        /// 要分配给新 Ghost 实例的 Ghost ID
        /// </summary>
        public int GhostID;
        /// <summary>
        /// 用于从 <see cref="GhostSpawnQueue"/> 单例上的临时 <see cref="SnapshotDataBuffer"/>
        /// 获取服务器首个 Snapshot 的字节偏移量
        /// </summary>
        public int DataOffset;
        /// <summary>
        /// 与实体关联的初始 Dynamic Buffer 数据大小
        /// </summary>
        public uint DynamicDataSize;
        /// <summary>
        /// 此 Ghost 在客户端生成时的 Tick
        /// 主要用于确定首次拥有数据的 Tick，避免在获得任何 Ghost 数据前就生成实体
        /// </summary>
        internal NetworkTick ClientSpawnTick;
        /// <summary>
        /// 此 Ghost 在服务器生成时的 Tick
        /// 对预测生成而言，应匹配此 Tick，因为关注的是服务器何时生成 Ghost
        /// 而不是服务器何时首次将 Ghost 发给客户端
        /// 使用此值也意味着不会把 Ghost 变为相关视为一次生成
        /// </summary>
        public NetworkTick ServerSpawnTick;
        /// <summary>
        /// 分类系统为新收到 Ghost 找到预测生成实体时分配的实体引用
        /// 分配此字段时还应将 <see cref="HasClassifiedPredictedSpawn"/> 设为 true
        /// 如果引用实体不为 <see cref="Entity.Null"/>，Ghost 类型必须设为 <see cref="Type.Predicted"/>
        /// </summary>
        public Entity PredictedSpawnEntity;
        /// <summary>
        /// Ghost 分类系统处理完此特定生成实例后应设为 true
        /// 这样本帧稍后运行的系统，例如默认分类系统，就不会再次处理它
        /// </summary>
        public bool HasClassifiedPredictedSpawn
        {
            get => m_HasClassifiedPredictedSpawn == 1;
            set => m_HasClassifiedPredictedSpawn = (byte)(value ? 1 : 0);
        }
        byte m_HasClassifiedPredictedSpawn;
        /// <summary>
        /// 仅对预生成 Ghost 有效
        /// 生成系统主要用它为因相关性变化而重新实例化的预生成 Ghost 重新分配 PrespawnGhostIndex 组件
        /// </summary>
        internal int PrespawnIndex;
        /// <summary>
        /// 仅对预生成 Ghost 有效，表示该 Ghost 所属的场景 Section
        /// </summary>
        internal  Hash128 SceneGUID;
        /// <summary>
        /// 仅对预生成 Ghost 有效
        /// 预生成 Ghost 因相关性变化等原因重新生成时，用于为 <see cref="SceneSection"/> Shared Component 重新分配正确索引
        /// Section 索引用于确保创建该 Ghost 的 SubScene 通过销毁全部场景实体的默认方式卸载时
        /// 预生成 Ghost 实例也会被销毁
        /// </summary>
        internal  int SectionIndex;
        /// <summary>
        /// 返回格式化信息的辅助方法
        /// </summary>
        /// <returns>格式化后的信息字符串</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString512Bytes ToFixedString()
        {
            FixedString32Bytes spawnType = SpawnType switch
            {
                Type.Interpolated => "Interpolated",
                Type.Predicted => "Predicted",
                _ => "Unknown",
            };
            return $"GhostSpawnBuffer[{spawnType}-{GhostType},GID:{GhostID},CST:{ClientSpawnTick.ToFixedString()},SST:{ServerSpawnTick.ToFixedString()}|predSpawn:{PredictedSpawnEntity.ToFixedString()},hasClassified:{m_HasClassifiedPredictedSpawn}|prespawnIdx:{PrespawnIndex}|sectionIdx:{SectionIndex}]";
        }
        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }

    /// <summary>
    /// <para>
    /// 包含所有对已生成 Ghost 进行分类的系统，在 <see cref="GhostReceiveSystem"/> 之后运行
    /// 自定义分类系统应加入此组更新
    /// </para>
    /// <code>
    /// [UpdateInGroup(typeof(GhostSpawnClassificationSystemGroup))]
    /// public partial struct MyCustomClassificationSystemGroup
    /// {
    ///    ...
    /// }
    /// </code>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation, WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostInputSystemGroup))]
    public partial class GhostSpawnClassificationSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    /// 默认 GhostSpawnClassificationSystem 会将 SpawnType 设为 GhostAuthoringComponent 中指定的默认值
    /// 除非其他分类逻辑已经设置 SpawnType
    /// 此系统还会检查 Ghost 所有者，为所有者预测 Ghost 正确设置生成类型
    /// 实现预测生成时，通常在 GhostSpawnClassificationSystem 之后添加系统
    /// 只检查 SpawnType 为 Predicted 的条目，找到匹配实体后设置 PredictedSpawnEntity
    /// 将预测生成系统放在默认系统之后，是为了确保所有者预测逻辑已经执行
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSpawnClassificationSystemGroup))]
    [CreateAfter(typeof(GhostCollectionSystem))]
    [CreateAfter(typeof(GhostReceiveSystem))]
    [BurstCompile]
    public partial struct GhostSpawnClassificationSystem : ISystem
    {
        private SnapshotDataLookupHelper m_spawnBufferHelper;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            m_spawnBufferHelper = new SnapshotDataLookupHelper(ref state,
                SystemAPI.GetSingletonEntity<GhostCollection>(),
                SystemAPI.GetSingletonEntity<SpawnedGhostEntityMap>());
            state.RequireForUpdate<NetworkId>();
            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<GhostSpawnQueue>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            m_spawnBufferHelper.Update(ref state);
            var classificationJob = new GhostSpawnClassification
            {
                SpawnBufferLookupHelper = m_spawnBufferHelper,
                networkId = SystemAPI.GetSingleton<NetworkId>().Value
            };
            state.Dependency = classificationJob.Schedule(state.Dependency);
        }
        [WithAll(typeof(GhostSpawnQueue))]
        [BurstCompile]
        partial struct GhostSpawnClassification : IJobEntity
        {
            public SnapshotDataLookupHelper SpawnBufferLookupHelper;
            public int networkId;
            public void Execute(DynamicBuffer<GhostSpawnBuffer> ghosts, in DynamicBuffer<SnapshotDataBuffer> data)
            {
                var spawnBufferLookup = SpawnBufferLookupHelper.CreateSnapshotBufferLookup();
                for (int i = 0; i < ghosts.Length; ++i)
                {
                    ref var ghost = ref ghosts.ElementAt(i);
                    if (ghost.SpawnType == GhostSpawnBuffer.Type.Unknown)
                    {
                        ghost.SpawnType = spawnBufferLookup.GetFallbackPredictionMode(ghost);
                        if(spawnBufferLookup.IsOwnerPredicted(ghost) && spawnBufferLookup.HasGhostOwner(ghost))
                        {
                            // PredictionOwnerOffset 指示所有者在 Snapshot 数据中的存储位置
                            var ghostOwner = spawnBufferLookup.GetGhostOwner(ghost, data);
                            if(ghostOwner == networkId)
                                ghost.SpawnType = GhostSpawnBuffer.Type.Predicted;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 默认 Ghost 生成分类系统会将预测生成实体与服务器 Snapshot 中同类型的新生成实体匹配
    /// 前提是两者生成 Tick 差值处于指定范围内，默认为 5 个 Tick
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSpawnClassificationSystemGroup), OrderLast = true)]
    [BurstCompile]
    internal partial struct DefaultGhostSpawnClassificationSystem : ISystem
    {
        BufferLookup<PredictedGhostSpawn> m_PredictedGhostSpawnLookup;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            m_PredictedGhostSpawnLookup = state.GetBufferLookup<PredictedGhostSpawn>();
            state.RequireForUpdate<GhostSpawnQueue>();
            state.RequireForUpdate<PredictedGhostSpawnList>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            m_PredictedGhostSpawnLookup.Update(ref state);
            if (!SystemAPI.TryGetSingleton(out ClientTickRate clientTickRate))
                clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            var classificationJob = new DefaultGhostSpawnClassificationJob
            {
                spawnListEntity = SystemAPI.GetSingletonEntity<PredictedGhostSpawnList>(),
                spawnListLookup = m_PredictedGhostSpawnLookup,
                acceptedTickPeriod = clientTickRate.DefaultClassificationAllowableTickPeriod,
            };
            state.Dependency = classificationJob.Schedule(state.Dependency);
        }

        [WithAll(typeof(GhostSpawnQueue))]
        [BurstCompile]
        partial struct DefaultGhostSpawnClassificationJob : IJobEntity
        {
            public Entity spawnListEntity;
            public BufferLookup<PredictedGhostSpawn> spawnListLookup;
            public uint acceptedTickPeriod;

            public void Execute(DynamicBuffer<GhostSpawnBuffer> ghosts)
            {
                var spawnList = spawnListLookup[spawnListEntity];
                for (int i = 0; i < ghosts.Length; ++i)
                {
                    ref var ghost = ref ghosts.ElementAt(i);
                    if (ghost.SpawnType != GhostSpawnBuffer.Type.Predicted || ghost.HasClassifiedPredictedSpawn || ghost.PredictedSpawnEntity != Entity.Null)
                        continue;
                    for (int j = 0; j < spawnList.Length; ++j)
                    {
                        ref readonly var predictedGhostSpawn = ref spawnList.ElementAt(j);
                        if (ghost.GhostType == predictedGhostSpawn.ghostType &&
                            math.abs(ghost.ServerSpawnTick.TicksSince(predictedGhostSpawn.spawnTick)) < acceptedTickPeriod)
                        {
                            ghost.PredictedSpawnEntity = predictedGhostSpawn.entity;
                            ghost.HasClassifiedPredictedSpawn = true;
                            //UnityEngine.Debug.Log($"Classification success! GID:{ghost.GhostID} sT:{predictedGhostSpawn.spawnTick.ToFixedString()} vs g.SST:{ghost.ServerSpawnTick.ToFixedString()} vs g.CST:{ghost.ClientSpawnTick.ToFixedString()} {predictedGhostSpawn.entity.ToFixedString()}!\n\n{ghost.ToFixedString()}\n{predictedGhostSpawn.ToFixedString()}");
                            spawnList.RemoveAtSwapBack(j);
                            break;
                        }
                    }

                    ghosts[i] = ghost;
                }
            }
        }
    }
}
