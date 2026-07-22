using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.NetCode.LowLevel.StateSave;
using Unity.Profiling;
using UnityEngine;

[assembly: RegisterGenericJobType(typeof(StateSaveJob<DirectStateSaveStrategy>))]
[assembly: RegisterGenericJobType(typeof(StateSaveJob<IndexedByGhostSaveStrategy>))]

namespace Unity.NetCode.LowLevel.StateSave
{
    // 若要处理非 Ghost 实体，索引就不应绑定到 SpawnedGhost，目前暂用该类型过渡
    internal struct SavedEntityID : IEquatable<SavedEntityID>
    {
        public SpawnedGhost value;

        public SavedEntityID(in GhostInstance ghostInstance)
        {
            value = new SpawnedGhost(ghostInstance);
        }
        // TODO 应使用若干前置位区分自定义 ID 与 Ghost ID
        // public SavedEntityID(int customID)
        // {
        //     value = new SpawnedGhost();
        //     value.ghostId = customID; // TODO 这是临时方案，若 SavedEntityID 不再专指 Ghost，直接存储 int 会更合适
        // }

        public bool Equals(SavedEntityID other)
        {
            return value.Equals(other.value);
        }

        public override bool Equals(object obj)
        {
            return obj is SavedEntityID other && Equals(other);
        }

        public override int GetHashCode()
        {
            return value.GetHashCode();
        }

        public override string ToString()
        {
            var worldToUse = ClientServerBootstrap.ClientWorld ?? ClientServerBootstrap.ServerWorld;
            string name = "";
            if (worldToUse != null)
            {
                using var singletonQuery = worldToUse.EntityManager.CreateEntityQuery(typeof(SpawnedGhostEntityMap));
                if (singletonQuery.HasSingleton<SpawnedGhostEntityMap>())
                {
                    var map = singletonQuery.GetSingleton<SpawnedGhostEntityMap>();
                    if (map.Value.ContainsKey(value))
                    {
                        var clientEntity = map.Value[value];
                        name = worldToUse.EntityManager.GetName(clientEntity);
                    }
                }
            }

            return $"Ghost:{name}:{value.ghostId}:spawnTick:{value.spawnTick}";
        }
    }

    /// <summary>
    /// 保存容器内实体组件区域所用的 Buffer 数据偏移和长度
    /// </summary>
    internal struct BufferHandle
    {
        /// <summary>
        /// Buffer 长度，即 Buffer 元素数量
        /// </summary>
        public int Length;

        /// <summary>
        /// 完整 Buffer 在组件数据之后的偏移，其大小为实体数量乘以 Buffer 长度
        /// </summary>
        public int Offset;
    }

    /// <summary>
    /// 用于配置状态保存的基础扩展方法，其他程序集可添加扩展方法以创建更多状态保存过滤器，例如 WithAllGhosts 可遍历所有 Ghost 类型并收集其组件
    /// </summary>
    internal static class WorldStateSaveExtensions
    {
        public static WorldStateSave WithRequiredTypes(this WorldStateSave self, in NativeHashSet<ComponentType> requiredTypesToSave)
        {
            foreach (var componentType in requiredTypesToSave)
            {
                self.RequiredTypesToSaveConfig.Add(componentType);
            }
            return self;
        }

        public static WorldStateSave WithOptionalTypes(this WorldStateSave self, in NativeHashSet<ComponentType> optionalTypesToSave)
        {

            foreach (var componentType in optionalTypesToSave)
            {
                self.OptionalTypesToSaveConfig.Add(componentType);
            }
            return self;
        }
    }

    // 根据指定的组件类型集合跟踪一个 World 的状态保存容器
    // 设计说明：该容器最终也应能在主线程创建并包含非 Entity 数据，例如任意结构体
    [DebuggerDisplay("Entity Count = {m_EntityCount}, allocation size = {m_AllocationSize} B")]
    internal unsafe struct WorldStateSave : IDisposable, IEnumerable<WorldStateSave.StateSaveEntry>
    {
        internal struct WorldSaveParallelWriter
        {
            public NativeParallelHashMap<SavedEntityID, (StateSaveContainer stateSave, IntPtr entityPtr)>.ParallelWriter entityIndexWriter;
            public NativeArray<StateSaveContainer> m_AllStateSaveContainers;

            static readonly  ProfilerMarker s_Marker = new ProfilerMarker("RegisterNewGhost");
            public void RegisterNewEntity(in SavedEntityID entity, in StateSaveContainer containerSave, int entIndex)
            {
                // TODO 考虑改到主线程执行，从而避免使用并行 HashMap
                using var a = s_Marker.Auto();
                var objAdrSpan = containerSave.GetObjectAdrInSave(entIndex);
                byte* objAdr = (byte*)UnsafeUtility.AddressOf(ref objAdrSpan[0]);
                entityIndexWriter.TryAdd(entity, (containerSave, new IntPtr(objAdr)));
            }
        }

        public bool Initialized { get; private set; }
        readonly void CheckInitialized() { if (!Initialized) throw new ObjectDisposedException($"{nameof(WorldStateSave)} not initialized, don't forget to call {nameof(Initialize)}"); }

        #region Main Allocation
        // Buffer：实体组件数据区域中只保存 Buffer Header，其中记录 Buffer 数据的偏移
        //         当前 Container/Chunk 的 Buffer 数据统一存放在所有组件数据之后，组件数据末尾即其起始偏移
        // Enableable：可启用类型会在组件数据之后用额外一个字节保存启用状态
        // 主内存分配
        // 主内存布局，其中 B 是可启用组件，C 是 Buffer 类型
        // |                         主内存分配                                                                                 |
        // |              Container                              | Buffer 数据 | Container      | Buffer 数据| Container           | // Container 指向主内存分配中的各个区段
        // | 组件类型列表          | 实体数据                     |             |                |            |                       // 每个区段先用 Header 保存类型列表，再保存逐实体数据，最后保存 Buffer 数据
        // |  A B C               |A1|   B1E   | C1B |A2|   B2E  | C1-12345    | C2B | AB       | C2-123     | |  | A C  |  | | |  | // 类型大小各异并按实体排序，E 表示启用状态，B 表示 Buffer Handle 信息
        [NativeDisableUnsafePtrRestriction]
        void* m_BaseStateSaveAddress;
        long m_AllocationSize;

        internal Span<byte> AsSpan
        {
            get
            {
                CheckInitialized();
                return new Span<byte>(m_BaseStateSaveAddress, (int)m_AllocationSize);
            }
        }
        Allocator m_Allocator;
        #endregion

        // 主内存分配由多个子 Container 构成
        NativeArray<StateSaveContainer> m_AllStateSaveContainers;
        // 用于直接访问实体数据的索引，避免遍历所有实体
        // TODO 可先确定最大 Ghost ID，再用长度为 maxCount 的 NativeArray 代替 HashMap，数组元素保存 Container 内的实际偏移
        // 这样可以避免高开销 HashMap，例如最大 Ghost ID 为 1000 时，使用长度为 1000 的数组，每项保存上述元组或指向内存分配某一区段的指针
        NativeParallelHashMap<SavedEntityID, (StateSaveContainer stateSave, IntPtr entityPtr)> m_EntityIndex;
        bool m_IsEmpty;
        public NativeHashSet<ComponentType> RequiredTypesToSaveConfig;
        public NativeHashSet<ComponentType> OptionalTypesToSaveConfig;
        NativeArray<ComponentType> m_RequiredTypesToSave; // 初始化后顺序不可随意改变
        NativeArray<ComponentType> m_OptionalTypesToSave; // 初始化后顺序不可随意改变
        [NativeDisableUnsafePtrRestriction] EntityQuery m_ToSaveQuery;
        int m_EntityCount;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        AtomicSafetyHandle m_SafetyHandle; // TODO 确保 Job 尚未完成时不能释放
        // TODO 确保 API 使用该 Handle 执行安全检查
#endif

        public int EntityCount
        {
            get
            {
                CheckInitialized();
                return m_EntityCount;
            }
            private set => m_EntityCount = value;
        }

        public int Size => (int)m_AllocationSize;

        static readonly ProfilerMarker s_PerChunkMarker = new("Per Chunk");
        static readonly ProfilerMarker s_MainStateAlloc = new ProfilerMarker("Main State Alloc");
        static readonly ProfilerMarker s_QueryMarker = new("To Arch Chunk Array");
        static readonly ProfilerMarker s_ChunkCalculation = new ProfilerMarker("Pre allocate destination memory");

        // 单 Chunk 状态保存，用于在主线程创建状态保存而无需调度 Job
        // TODO 确认是否确有必要，例如用于定位 System 内的自定义性能追踪区段
        // public WorldStateSave(int allocationSizeBytes, int entityCount, in NativeArray<ComponentType> componentTypes, Allocator allocator)
        // {
        //     m_AllStateSaveContainers = new(1, allocator);
        //     m_AllStateSaveContainers[0] = new StateSaveContainer(componentTypes, 0, entityCount, allocator);
        //     m_GhostIndex = new(10, allocator);
        //     m_IsEmpty = false;
        //     m_RequiredTypesToSave = default;
        //     m_OptionalTypesToSave = default;
        //     m_ToSaveQuery = default;
        //     BaseStateSaveAddress = UnsafeUtility.Malloc(allocationSizeBytes, 16, allocator);
        //     m_AllocationSize = allocationSizeBytes;
        //     m_Allocator = allocator;
        //     Initialized = true;
        // }

        public WorldStateSave(Allocator allocator)
        {
            m_Allocator = allocator;
            m_BaseStateSaveAddress = null;
            m_AllocationSize = 0;
            m_AllStateSaveContainers = default;
            m_EntityIndex = default;
            m_IsEmpty = false;
            RequiredTypesToSaveConfig = new (1, m_Allocator);
            OptionalTypesToSaveConfig = new (1, m_Allocator);
            m_RequiredTypesToSave = default;
            m_OptionalTypesToSave = default;
            m_ToSaveQuery = default;
            m_EntityCount = 0;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_SafetyHandle = default;
#endif
            Initialized = false;
        }

        /// <inheritdoc cref="Initialize"/>
        /// 使用默认策略初始化
        public WorldStateSave Initialize(ref SystemState state)
        {
            return Initialize(ref state, new DirectStateSaveStrategy());
        }

        static readonly ProfilerMarker s_InitializeMarker = new("WorldStateSave.Initialize");
        // 不能放在构造函数中，因为 C# 构造函数不支持泛型类型参数
        /// <summary>
        /// 使用指定策略初始化 World 状态保存
        /// </summary>
        /// <param name="state"></param>
        /// <param name="requiredTypesToSave"></param>
        /// <param name="optionalTypesToSave">未传入必需类型时，内部使用 WithAny 过滤；否则这些类型为真正的可选项，即使没有实体包含它们，也仍可通过必需类型匹配</param>
        /// <param name="stateSaveStrategy"></param>
        /// <param name="allocator"></param>
        /// <typeparam name="TStrategy"></typeparam>
        /// <returns></returns>
        public WorldStateSave Initialize<TStrategy>(ref SystemState state, in TStrategy stateSaveStrategy) where TStrategy : IStateSaveStrategy
        {
            using var a = s_InitializeMarker.Auto();

            if (Initialized)
                throw new InvalidOperationException($"{nameof(WorldStateSave)} already initialized, make sure to call {nameof(Reset)} if you intend to reuse the allocation and not dispose it.");
            if (this.OptionalTypesToSaveConfig.Count == 0 && this.RequiredTypesToSaveConfig.Count == 0)
            {
                throw new ArgumentException($"you need to specify at least one required or optional type to save. Please use {OptionalTypesToSaveConfig} or {nameof(RequiredTypesToSaveConfig)}");
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            this.m_SafetyHandle = AtomicSafetyHandle.Create();
#endif
            stateSaveStrategy.UpdateTypesToTrack(ref this.RequiredTypesToSaveConfig, ref this.OptionalTypesToSaveConfig);

            // 为本次状态保存创建 EntityQuery
            if (RequiredTypesToSaveConfig.Count > 0)
                this.m_RequiredTypesToSave = RequiredTypesToSaveConfig.ToNativeArray(m_Allocator);
            else
                this.m_RequiredTypesToSave = new(0, m_Allocator);
            if (OptionalTypesToSaveConfig.Count > 0)
                this.m_OptionalTypesToSave = OptionalTypesToSaveConfig.ToNativeArray(m_Allocator);
            else
                this.m_OptionalTypesToSave = new(0, m_Allocator);

            for (int i = 0; i < m_RequiredTypesToSave.Length; i++)
            {
                if (m_RequiredTypesToSave[i].AccessModeType != ComponentType.AccessMode.ReadOnly)
                {
                    var t = m_RequiredTypesToSave[i];
                    t.AccessModeType = ComponentType.AccessMode.ReadOnly;
                    m_RequiredTypesToSave[i] = t;
                }
            }
            for (int i = 0; i < m_OptionalTypesToSave.Length; i++)
            {
                if (m_OptionalTypesToSave[i].AccessModeType != ComponentType.AccessMode.ReadOnly)
                {
                    var t = m_OptionalTypesToSave[i];
                    t.AccessModeType = ComponentType.AccessMode.ReadOnly;
                    m_OptionalTypesToSave[i] = t;
                }
            }

            var requiredTypesList = new NativeList<ComponentType>(m_RequiredTypesToSave.Length, Allocator.Temp);
            requiredTypesList.AddRange(m_RequiredTypesToSave);
            var optionalTypesList = new NativeList<ComponentType>(m_OptionalTypesToSave.Length, Allocator.Temp);
            optionalTypesList.AddRange(m_OptionalTypesToSave);
            foreach (var optionalType in m_OptionalTypesToSave)
            {
                if (m_RequiredTypesToSave.Contains(optionalType))
                {
                    throw new ArgumentException($"Duplicate type found in both required and optional types sets {optionalType}. Types can only be one of required or optional.");
                }
            }

            // 保存组件时必须控制必需类型与可选类型的顺序，因此不对外暴露该 Builder，而是仅提供必需和可选等有限过滤方式
            using var builder = new EntityQueryBuilder(Allocator.Temp);
            // WithPresent 会包含组件已禁用的实体，WithAll 则会在任一可启用组件被禁用时完全排除该实体
            if (requiredTypesList.Length != 0)
                builder.WithPresent(ref requiredTypesList);
            else
                builder.WithAny(ref optionalTypesList); // 已遍历全部必需类型，并在 IJobChunk 中检查可选类型；若要跟踪组件集合完全不同且没有交集的实体，可不设置必需类型并仅遍历 WithAny
            m_ToSaveQuery = state.EntityManager.CreateEntityQuery(builder);

            this.m_IsEmpty = m_ToSaveQuery.IsEmpty;
            var chunkCount = m_ToSaveQuery.CalculateChunkCount();
            m_EntityCount =  m_ToSaveQuery.CalculateEntityCount();
            m_AllStateSaveContainers = new (chunkCount, m_Allocator);
            m_EntityIndex = new(m_EntityCount, m_Allocator);

            // 预先计算并分配目标状态保存内存
            // 在主线程执行持久分配更快，因此在此完成全部分配，相关讨论见 https://unity.slack.com/archives/C3H8JSB5E/p1743427468083499
            // Unity 6 中仍需如此处理，Unity 7 将改进这一点
            s_ChunkCalculation.Begin();
            long totalSizeBytesNeeded = 0;
            long singleEntityRequiredSize = 0;
            // 先计算 requiredTypes 的组件大小，Buffer 只计入 Header 而不计入数据
            for (int i = 0; i < m_RequiredTypesToSave.Length; i++)
            {
                if (m_RequiredTypesToSave[i].IsBuffer)
                    singleEntityRequiredSize += sizeof(BufferHandle);
                else
                    singleEntityRequiredSize += TypeManager.GetTypeInfo(m_RequiredTypesToSave[i].TypeIndex).SizeInChunk;
                if (m_RequiredTypesToSave[i].IsEnableable)
                    singleEntityRequiredSize++; // 在组件数据末尾用一个字节保存启用状态，TODO 可考虑改为保存 Chunk 的位域
            }

            // 这里假定计算完成后立即执行 Job，期间不会发生结构变更
            s_QueryMarker.Begin();
            using var chunks = m_ToSaveQuery.ToArchetypeChunkArray(Allocator.Temp);
            s_QueryMarker.End();
            EntityArchetype previousArchetype = default; // Chunk 按 Archetype 排序，若前后 Archetype 相同即可复用尺寸并走快速路径
            using var allComponentTypesInContainer = new NativeList<ComponentType>(Allocator.Temp);
            var singleEntityOptionalSize = 0;
            s_PerChunkMarker.Begin();
            for (int i = 0; i < chunks.Length; i++)
            {
                ArchetypeChunk chunk = chunks[i];
                var entityCountInChunk = chunk.Count;
                // 计算当前 Chunk 中可选组件所需的大小
                if (chunk.Archetype != previousArchetype)
                {
                    previousArchetype = chunk.Archetype;
                    using var chunkTypes = chunk.Archetype.GetComponentTypes(Allocator.Temp);
                    allComponentTypesInContainer.Clear();
                    allComponentTypesInContainer.AddRange(m_RequiredTypesToSave);
                    singleEntityOptionalSize = 0;
                    for (int j = 0; j < chunkTypes.Length; j++)
                    {
                        var t = chunkTypes[j];
                        t.AccessModeType = ComponentType.AccessMode.ReadOnly; // 供下方 Contains() 比较使用
                        if (m_OptionalTypesToSave.Contains(t))
                        {
                            if (t.IsBuffer)
                                singleEntityOptionalSize += sizeof(BufferHandle);
                            else
                                singleEntityOptionalSize += TypeManager.GetTypeInfo(t.TypeIndex).SizeInChunk;
                            if (t.IsEnableable)
                                singleEntityOptionalSize++;
                            allComponentTypesInContainer.Add(t);
                        }
                    }
                }

                // 收集 Buffer 类型并完成其依赖，因为后续需要读取数据
                var bufferTypes = new NativeList<ComponentType>(Allocator.Temp);
                for (int j = 0; j < allComponentTypesInContainer.Length; j++)
                {
                    var type = allComponentTypesInContainer[j];
                    if (!type.IsBuffer) continue;
                    bufferTypes.Add(type);
                }
                var bufferQueryBuilder = new EntityQueryBuilder(Allocator.Temp);
                bufferQueryBuilder.WithAny(ref bufferTypes);
                using var bufferQuery = state.EntityManager.CreateEntityQuery(bufferQueryBuilder);
                bufferQuery.CompleteDependency();

                // 计算该 Container 使用的 Buffer 大小，逐类型检查每个实体包含的元素数量
                var bufferSizeForComponentTypes = 0;
                for (int j = 0; j < bufferTypes.Length; j++)
                {
                    var type = bufferTypes[j];
                    var typeHandle = state.EntityManager.GetDynamicComponentTypeHandle(type);
                    var bufferData = chunk.GetUntypedBufferAccessor(ref typeHandle);
                    for (int k = 0; k < chunk.Count; k++)
                        bufferSizeForComponentTypes += bufferData.GetBufferLength(k) * TypeManager.GetTypeInfo(type.TypeIndex).ElementSize;
                }

                // 每个状态保存 Chunk 实际都指向同一大块内存中的连续区段
                // 先用偏移初始化 Container，取得实际内存后再通过 InitializeSaveAddress() 设置地址
                var containerOffset = totalSizeBytesNeeded;
                var currentStateSave = new StateSaveContainer(allComponentTypesInContainer.AsArray(), containerOffset, entityCountInChunk, m_Allocator, state.WorldUnmanaged, chunk, bufferSizeForComponentTypes);
                m_AllStateSaveContainers[i] = currentStateSave;

                totalSizeBytesNeeded += entityCountInChunk * (singleEntityOptionalSize + singleEntityRequiredSize) + bufferSizeForComponentTypes + currentStateSave.HeaderSize;
            }
            s_PerChunkMarker.End();

            s_MainStateAlloc.Begin();

            bool reuseAllocation = false;
            if (m_BaseStateSaveAddress != null && m_AllocationSize >= totalSizeBytesNeeded)
            {
                // 已分配的内存足够使用
                reuseAllocation = true;
                if (m_AllocationSize > totalSizeBytesNeeded * 2)
                {
                    // 已分配内存过多，为避免长期只增不减而执行收缩
                    reuseAllocation = false;
                }
            }
            if (!reuseAllocation && m_BaseStateSaveAddress != null)
            {
                // 无法复用原内存，因此释放它以避免泄漏
                UnsafeUtility.Free(m_BaseStateSaveAddress, m_Allocator);
            }

            if (reuseAllocation)
            {
                m_AllocationSize = totalSizeBytesNeeded;
            }
            else
            {
                // 为本次状态保存分配主内存
                m_BaseStateSaveAddress = UnsafeUtility.Malloc(totalSizeBytesNeeded, 16, m_Allocator);
                m_AllocationSize = totalSizeBytesNeeded;
            }

            s_MainStateAlloc.End();
            for (int i = 0; i < m_AllStateSaveContainers.Length; i++)
            {
                // 主内存已分配，让各 Container 指向对应区段
                var stateSaveContainer =  m_AllStateSaveContainers[i];
                stateSaveContainer.InitializeSaveAddress((byte*)m_BaseStateSaveAddress);
                m_AllStateSaveContainers[i] = stateSaveContainer;
            }
            s_ChunkCalculation.End();
            requiredTypesList.Dispose();
            optionalTypesList.Dispose();

            Initialized = true;

            return this;
        }

        // 释放内部元数据，但保留主内存供后续复用
        public void Reset()
        {
            CheckInitialized();
            // 保留主内存并重置其他所有内容
            foreach (var oneContainer in m_AllStateSaveContainers)
            {
                oneContainer.Dispose();
            }
            m_AllStateSaveContainers.Dispose();
            m_RequiredTypesToSave.Dispose();
            m_OptionalTypesToSave.Dispose();
            m_EntityIndex.Dispose();
            m_ToSaveQuery.Dispose();
            Initialized = false;
        }

        public void Dispose()
        {
            CheckInitialized();
            UnsafeUtility.Free(m_BaseStateSaveAddress, this.m_Allocator);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.Release(m_SafetyHandle);
#endif
            m_BaseStateSaveAddress = null;
            foreach (var oneContainer in m_AllStateSaveContainers)
            {
                oneContainer.Dispose();
            }
            m_AllStateSaveContainers.Dispose();
            m_RequiredTypesToSave.Dispose();
            m_OptionalTypesToSave.Dispose();
            m_EntityIndex.Dispose();
            m_ToSaveQuery.Dispose();
            Initialized = false;
        }

        // TODO 支持主线程状态保存
        // public void RegisterNewGhost(in SavedEntityID savedEntityID, int entIndex)
        // {
        //     Assert.IsTrue(m_AllStateSaveContainers.Length == 1, "Assumes this API is used for single chunk, single thread state saves. Else please use the ParallelWriter");
        //     var containerSave = m_AllStateSaveContainers[0];
        //     var objAdrSpan = containerSave.GetObjectAdrInSave(entIndex);
        //     fixed (byte* objAdr = objAdrSpan)
        //     {
        //         var res = m_GhostIndex.TryAdd(savedEntityID, (containerSave, new IntPtr(objAdr)));
        //     }
        // }
        //
        // public void SaveComponentData<T>(int entityIndex, T componentData) where T : struct
        // {
        //     m_AllStateSaveContainers[0].SaveCompForEntityIndex(entityIndex, ComponentType.ReadOnly<T>(), (byte*)UnsafeUtility.AddressOf(ref componentData));
        // }

        internal JobHandle ScheduleStateSaveJob(ref SystemState state)
        {
            return ScheduleStateSaveJob(ref state, new DirectStateSaveStrategy());
        }

        /// <summary>
        /// 调度状态保存 Job
        /// </summary>
        /// <param name="state"></param>
        /// <param name="stateSaveStrategy">保存单个组件所用的策略，可用于跳过特定实体或执行建立索引等附加操作</param>
        /// <typeparam name="TStrategy"></typeparam>
        /// <returns></returns>
        internal JobHandle ScheduleStateSaveJob<TStrategy>(ref SystemState state, TStrategy stateSaveStrategy) where TStrategy : IStateSaveStrategy
        {
            CheckInitialized();
            if (m_IsEmpty) return state.Dependency;

            var dynamicHandles = new DynamicTypeList();
            using var typesToTrack = new NativeList<ComponentType>(Allocator.Temp);
            typesToTrack.AddRange(m_RequiredTypesToSave);
            typesToTrack.AddRange(m_OptionalTypesToSave);
            DynamicTypeList.PopulateListFromArray(ref state, typesToTrack.AsArray(), readOnly: true, ref dynamicHandles);
            var job = new StateSaveJob<TStrategy>()
            {
                dynamicTypeList = dynamicHandles,
                requiredTypes = m_RequiredTypesToSave,
                optionalTypes = m_OptionalTypesToSave,
                fullWorldStateSave = this.GetParallelWriter(),
                stateSaveStrategy = stateSaveStrategy,
                entityType = state.GetEntityTypeHandle(),
                entityStorageInfo = state.GetEntityStorageInfoLookup()
            };
            var dep = job.ScheduleParallelByRef(m_ToSaveQuery, state.Dependency);
            return dep;
        }

        private WorldSaveParallelWriter GetParallelWriter()
        {
            CheckInitialized();
            return new WorldSaveParallelWriter() { m_AllStateSaveContainers = this.m_AllStateSaveContainers, entityIndexWriter = this.m_EntityIndex.AsParallelWriter() };
        }

        public readonly bool TryGetComponentData<T>(SavedEntityID entity, out T componentData) where T : struct
        {
            CheckInitialized();
            var indexEntry = this.m_EntityIndex[entity];
            return indexEntry.stateSave.TryGetSavedDataForPtr<T>((byte*)indexEntry.entityPtr, out componentData);
        }

        public readonly bool TryGetComponentData(SavedEntityID savedEntityID, ComponentType type, out byte* componentData)
        {
            CheckInitialized();
            var indexEntry = this.m_EntityIndex[savedEntityID];
            return indexEntry.stateSave.TryGetSavedDataForPtr((byte*)indexEntry.entityPtr, type, out componentData);
        }

        public bool HasComponent(SavedEntityID entity, ComponentType componentType)
        {
            CheckInitialized();
            var indexEntry = this.m_EntityIndex[entity];
            componentType.AccessModeType = ComponentType.AccessMode.ReadOnly;
            var containerStateSave = indexEntry.stateSave;
            foreach (var type in containerStateSave.ComponentTypesListHeader)
            {
                if (type == componentType) return true;
            }

            return false;
        }

        public NativeArray<SavedEntityID> GetAllEntities(Allocator allocator)
        {
            CheckInitialized();
            return m_EntityIndex.GetKeyArray(allocator);
        }

        public bool Exists(SavedEntityID savedEntityID)
        {
            CheckInitialized();
            return m_EntityIndex.ContainsKey(savedEntityID);
        }

        public readonly NativeArray<ComponentType> GetComponentTypes(SavedEntityID savedEntityID)
        {
            CheckInitialized();
            var containerStateSave = m_EntityIndex[savedEntityID].stateSave;
            byte* typesAdr = (byte*)UnsafeUtility.AddressOf(ref containerStateSave.ComponentTypesListHeader[0]);
            var toReturn = CollectionHelper.ConvertExistingDataToNativeArray<ComponentType>(typesAdr, containerStateSave.ComponentTypesListHeader.Length, Allocator.None); // 该内存并非由数组分配且不应由调用方释放，因此使用 Allocator.None

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref toReturn, m_SafetyHandle); // 复用 Safety Handle，避免每次创建临时实例
#endif
            return toReturn;
        }

        public struct StateSaveEntry : IEnumerable<StateSaveEntry.SavedComponentData>, IEnumerator<StateSaveEntry.SavedComponentData>
        {
            // TODO 增加直接获取指定组件的方式，例如 GetComponentData<T>()，可用于在恢复 Ghost 前读取 GhostInstance 元数据
            public byte* entityBaseAdr;
            public byte* containerBaseAdr;
            public NativeArray<ComponentType> types; // 指向主内存中的现有数据，不应修改数组内容

            int m_CurrentIndex;
            int m_CurrentOffset;

            internal static int SizeInSave(ComponentType type)
            {
                int enabledBitSize = 0;
                if (type.IsEnableable)
                    enabledBitSize = 1;
                if (type.IsBuffer)
                    return sizeof(BufferHandle) + enabledBitSize;
                return TypeManager.GetTypeInfo(type.TypeIndex).SizeInChunk + enabledBitSize;
            }

            public struct SavedComponentData
            {
                public byte* ComponentAdr;
                public int Length;
                public ComponentType Type;
                public bool Enabled;

                public void ToConcrete<T>(out T data) where T : struct
                {
                    UnsafeUtility.CopyPtrToStructure(ComponentAdr, out data);
                }

                public void ToConcrete<T>(ref NativeList<T> data) where T : unmanaged, IBufferElementData
                {
                    var elementSize = TypeManager.GetTypeInfo(Type.TypeIndex).ElementSize;
                    for (int i = 0; i < Length; ++i)
                    {
                        UnsafeUtility.CopyPtrToStructure(ComponentAdr + (i*elementSize), out T element);
                        data.Add(element);
                    }
                }
            }

            void InitIterator()
            {
                m_CurrentIndex = -1;
            }

            public void Dispose()
            {

            }

            public StateSaveEntry GetEnumerator()
            {
                InitIterator();
                return this;
            }
            IEnumerator<SavedComponentData> IEnumerable<SavedComponentData>.GetEnumerator()
            {
                InitIterator();
                return this;
            }
            IEnumerator IEnumerable.GetEnumerator()
            {
                InitIterator();
                return this;
            }

            public bool MoveNext()
            {
                m_CurrentIndex++;
                if (m_CurrentIndex < types.Length && m_CurrentIndex > 0)
                {
                    m_CurrentOffset += SizeInSave(types[m_CurrentIndex - 1]); // 累加前一类型的大小以得到当前偏移
                }
                return m_CurrentIndex < types.Length;
            }
            public void Reset()
            {
                throw new NotImplementedException();
            }

            public SavedComponentData Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    var address = entityBaseAdr + m_CurrentOffset;
                    var type = types[m_CurrentIndex];
                    var length = 0;
                    byte enabledByte = 0;
                    byte* enabledAddress;
                    if (type.IsBuffer)
                    {
                        enabledAddress = address + sizeof(BufferHandle);
                        var bufHeader = (BufferHandle*)address;
                        length = bufHeader->Length;
                        address = containerBaseAdr + bufHeader->Offset;
                    }
                    else
                    {
                        enabledAddress = address + TypeManager.GetTypeInfo(type.TypeIndex).SizeInChunk;
                    }
                    if (type.IsEnableable)
                        UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref enabledByte), enabledAddress, 1);

                    return new SavedComponentData { ComponentAdr = address, Length = length, Type = type, Enabled = enabledByte == 1};
                }
            }

            object IEnumerator.Current => Current;
        }

        public struct StateIterator : IEnumerator<StateSaveEntry>
        {
            int m_CurrentContainerIndex;
            int m_CurrentEntityIndexInContainer;
            NativeArray<StateSaveContainer> m_AllContainers;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            readonly AtomicSafetyHandle m_SafetyHandle; // TODO 考虑让 World 状态保存的主内存也使用该 Handle
#endif
            readonly WorldStateSave m_ParentStateSave;

            public StateIterator(WorldStateSave parentStateSave)
            {
                m_ParentStateSave = parentStateSave;
                m_ParentStateSave.CheckInitialized();
                m_AllContainers = parentStateSave.m_AllStateSaveContainers;
                m_CurrentContainerIndex = 0;
                m_CurrentEntityIndexInContainer = -1;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_SafetyHandle = AtomicSafetyHandle.Create();
#endif
            }
            public bool MoveNext()
            {
                m_ParentStateSave.CheckInitialized();
                if (m_AllContainers.Length == 0) return false;
                m_CurrentEntityIndexInContainer++;
                if (m_CurrentEntityIndexInContainer >= CurrentContainer.EntityCount)
                {
                    m_CurrentContainerIndex++;
                    m_CurrentEntityIndexInContainer = 0;
                }

                if (m_CurrentContainerIndex >= m_AllContainers.Length) return false;

                return true;
            }

            StateSaveContainer CurrentContainer => m_AllContainers[m_CurrentContainerIndex];
            public void Reset()
            {
                throw new NotImplementedException(); // 根据 Microsoft 文档，Reset 仅用于 COM 互操作，其他场景不应调用

                // m_CurrentContainerIndex = 0;
                // m_CurrentEntityIndexInContainer = -1;
            }

            public StateSaveEntry Current
            {
                get
                {
                    var currentContainer = CurrentContainer;
                    var componentTypesSpan = currentContainer.ComponentTypesListHeader;
                    var saveContainerSpan = currentContainer.StateSave;
                    NativeArray<ComponentType> typesForCurrentContainer;

                    ComponentType* typesAdr = (ComponentType*)UnsafeUtility.AddressOf(ref componentTypesSpan[0]);
                    typesForCurrentContainer = CollectionHelper.ConvertExistingDataToNativeArray<ComponentType>(typesAdr, componentTypesSpan.Length, Allocator.None); // 使用 Allocator.None，因此不会执行分配

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref typesForCurrentContainer, m_SafetyHandle); // 所有生成的 NativeArray 共用同一个 Safety Handle
#endif

                    var currentAsSpan = saveContainerSpan.Slice(m_CurrentEntityIndexInContainer * currentContainer.SingleEntitySize, currentContainer.SingleEntitySize);
                    byte* currentAdr = (byte*)UnsafeUtility.AddressOf(ref currentAsSpan[0]);
                    byte* containerAdr = (byte*)UnsafeUtility.AddressOf(ref saveContainerSpan[0]);

                    return new() { entityBaseAdr = currentAdr, containerBaseAdr = containerAdr,types = typesForCurrentContainer };
                }
            }

            object IEnumerator.Current => Current;

            public void Dispose()
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.Release(m_SafetyHandle);
#endif
            }
        }

        // 为兼容 Burst，GetEnumerator 需要显式返回类型
        public StateIterator GetEnumerator()
        {
            return new StateIterator(this);
        }

        IEnumerator<StateSaveEntry> IEnumerable<StateSaveEntry>.GetEnumerator()
        {
            return new StateIterator(this);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new StateIterator(this);
        }
    }

    // 保存时与 Chunk 一一对应，每个 Chunk 的内容复制到一个 Container，而 Container 只是指向 World 状态保存中某个地址的智能指针
    internal unsafe struct StateSaveContainer : IDisposable
    {
        // 数据布局
        // Header -> ComponentType 列表
        // Data -> 实体列表，每个实体依次保存相同的 n 个组件数据，例如 [实体 1 的 compA、compB、compC，实体 2 的 compA、compB、compC]
        byte* m_ContainerStateSaveAdr;
        internal int HeaderSize;

        public readonly Span<ComponentType> ComponentTypesListHeader => new(m_ContainerStateSaveAdr, HeaderSize / UnsafeUtility.SizeOf<ComponentType>());
        public readonly Span<byte> StateSave => new(m_ContainerStateSaveAdr + HeaderSize, SingleEntitySize * EntityCount + TotalBufferSize);

        readonly byte* GetContainerSaveDataAddress
        {
            get
            {
                CheckInitialized();
                return m_ContainerStateSaveAdr + HeaderSize;
            }
        }
        long m_ContainerOffsetInParentAllocation; // 上述指针指向不归该 Container 所有的内存，因此需要记录它在该内存中的偏移
        bool m_Initialized;
        int m_NextBufferOffset;

        readonly void CheckInitialized() { if (!m_Initialized) throw new ObjectDisposedException($"Container disposed, make sure you call {nameof(InitializeSaveAddress)}"); }

        internal int SingleEntitySize;
        internal int TotalBufferSize;
        public readonly int EntityCount;

        static readonly ProfilerMarker s_StateSaveConstructorMarker = new($"{nameof(StateSaveContainer)} Constructor");
        internal StateSaveContainer(in NativeArray<ComponentType> componentTypes, long containerOffsetInParentAllocation, int entityCount, in Allocator allocator, WorldUnmanaged world, ArchetypeChunk chunk, int totalBufferSize)
        {
            using var a = s_StateSaveConstructorMarker.Auto();
            var offset = 0;
            var headerSize = 0;
            foreach (var type in componentTypes)
            {
                var dotsTypeInfo = TypeManager.GetTypeInfo(type.TypeIndex);
                if (!type.IsBuffer)
                {
                    offset += dotsTypeInfo.SizeInChunk;
                }
                else
                {
                    offset += sizeof(BufferHandle); // 在实体组件数据区域中为每种 Buffer 保存长度和偏移
                }
                if (type.IsEnableable)
                    offset++;
                headerSize += UnsafeUtility.SizeOf<ComponentType>(); // Job 执行时会把组件类型列表写入 Header，当前尚不知道数据地址，无法立即保存
            }

            HeaderSize = headerSize;
            SingleEntitySize = offset;
            TotalBufferSize = totalBufferSize;
            EntityCount = entityCount;

            this.m_ContainerStateSaveAdr = null;
            this.m_ContainerOffsetInParentAllocation = containerOffsetInParentAllocation;
            m_NextBufferOffset = 0;
            m_Initialized = false; // 只有调用 InitializeSaveAddress 后才算初始化
        }

        internal void InitializeSaveAddress(byte* baseAddress)
        {
            this.m_ContainerStateSaveAdr = baseAddress + m_ContainerOffsetInParentAllocation;
            m_Initialized = true;
        }

        public void Dispose()
        {
            CheckInitialized();
            // Container 不拥有关联内存，因此此处不释放内存
            m_Initialized = false;
        }

        public void SaveCompForEntityIndex(in int entIndex, in ComponentType componentType, in byte* chunkCompData, bool enableBitIsSet)
        {
            CheckInitialized();
            var found = TryGetOffsetForComponentType(componentType, out var compOffset);
            // TODO 缓存这些偏移与大小
            var size = TypeManager.GetTypeInfo(componentType.TypeIndex).SizeInChunk;
            var dstAdrSpan = GetObjectAdrInSave(entIndex).Slice(compOffset, size);
            byte* srcAdr = chunkCompData + entIndex * size;

            byte* dstAdr = (byte*)UnsafeUtility.AddressOf(ref dstAdrSpan[0]);
            UnsafeUtility.MemCpy(dstAdr, srcAdr, size);
            if (componentType.IsEnableable)
            {
                // 启用状态写入组件数据之后的一个字节
                var enableBitAddress = dstAdr + size;
                UnsafeUtility.MemCpy(enableBitAddress, &enableBitIsSet, 1);
            }
        }

        public void SaveBufferForEntityIndex(in WorldStateSave.WorldSaveParallelWriter fullWorldStateSave, in int entIndex, in ComponentType componentType, in byte* chunkCompData, int bufferElementCount, bool enableBitIsSet)
        {
            CheckInitialized();
            var found = TryGetOffsetForComponentType(componentType, out var compOffset);
            var size = TypeManager.GetTypeInfo(componentType.TypeIndex).ElementSize * bufferElementCount;

            var dstAdrSpan = GetObjectAdrInSave(entIndex).Slice(compOffset, sizeof(BufferHandle));
            byte* dstAdr = (byte*)UnsafeUtility.AddressOf(ref dstAdrSpan[0]);

            var bufferHeader = (BufferHandle*)dstAdr;
            if (bufferElementCount == 0)
            {
                bufferHeader->Length = 0;
                bufferHeader->Offset = -1;
                return;
            }

            if (componentType.IsEnableable)
            {
                // 启用状态写入 Buffer Header 数据之后的一个字节
                var enableBitAddress = dstAdr + sizeof(BufferHandle);
                UnsafeUtility.MemCpy(enableBitAddress, &enableBitIsSet, 1);
            }

            // 从上一个已登记 Buffer 的末尾开始，以便按实体索引连续写入 Buffer 区域
            if (m_NextBufferOffset == 0)
                m_NextBufferOffset = EntityCount * SingleEntitySize;  // 首次复制 Buffer 时初始化为 Buffer 区域起点
            bufferHeader->Offset = m_NextBufferOffset;
            bufferHeader->Length = bufferElementCount;
            m_NextBufferOffset += size;

            dstAdr = GetContainerSaveDataAddress + bufferHeader->Offset;
            UnsafeUtility.MemCpy(dstAdr, chunkCompData, size);
        }

        internal readonly Span<byte> GetObjectAdrInSave(int entIndex)
        {
            CheckInitialized();
            return new (GetContainerSaveDataAddress + entIndex * SingleEntitySize, SingleEntitySize);
        }

        internal bool TryGetSavedDataForPtr<T>(byte* entityPtr, out T data) where T : struct
        {
            CheckInitialized();
            var found = TryGetSavedDataForPtr(entityPtr, ComponentType.ReadOnly<T>(), out var dataPtr);
            if (found)
                UnsafeUtility.CopyPtrToStructure(dataPtr, out data);
            else
                data = default;
            return found;
        }

        internal bool TryGetSavedDataForPtr(byte* entityPtr, ComponentType type, out byte* data)
        {
            CheckInitialized();
            var found = TryGetOffsetForComponentType(type, out var offset);
            data = entityPtr + offset;
            return found;
        }

        // TODO 将该信息也保存到 Header，目前这种查找方式性能较差
        private bool TryGetOffsetForComponentType(ComponentType type, out int offset)
        {
            CheckInitialized();
            type.AccessModeType = ComponentType.AccessMode.ReadOnly;
            offset = 0;
            foreach (var containedType in this.ComponentTypesListHeader)
            {
                if (containedType == type)
                    return true;

                // 组件区域只保存 Buffer Header，其中包含指向 Buffer 区域的偏移
                if (containedType.IsBuffer)
                    offset += sizeof(BufferHandle);
                else
                    offset += TypeManager.GetTypeInfo(containedType.TypeIndex).SizeInChunk;
                if (containedType.IsEnableable)
                    offset++;
            }

            offset = -1;
            return false;
        }

        public void AddComponentType(int index, ComponentType currentCompType)
        {
            CheckInitialized();
            if (index >= ComponentTypesListHeader.Length)
                UnityEngine.Debug.LogError($"Component type index out of bounds while adding component {currentCompType}");
            else
                ComponentTypesListHeader[index] = currentCompType;
        }
    }

    internal unsafe interface IStateSaveStrategy
    {
        void UpdateTypesToTrack(ref NativeHashSet<ComponentType> requiredTypes, ref NativeHashSet<ComponentType> optionalTypes);
        void SaveEntity(ref StateSaveContainer currentStateSave, ref WorldStateSave.WorldSaveParallelWriter fullWorldStateSave, in ArchetypeChunk chunk, in int unfilteredChunkIndex, in int entIndex, in ComponentType currentCompType, in byte* toCopyPtr, in int bufferElementCount, in int compIndex, bool enableBitIsSet);
    }

    /// <summary>
    /// 直接保存到已分配内存，不执行额外操作
    /// 不建立索引，可减少写入 HashMap 的开销，在不需要索引时提升状态保存性能
    /// </summary>
    internal unsafe struct DirectStateSaveStrategy : IStateSaveStrategy
    {
        static readonly ProfilerMarker s_Marker = new("DefaultStateSaveStrategy.SaveEntity");
        static readonly ProfilerMarker s_MarkerProfiler = new("DefaultStateSaveStrategy.SaveEntity.profilerMarker");
        public void SaveEntity(ref StateSaveContainer currentStateSave, ref WorldStateSave.WorldSaveParallelWriter fullWorldStateSave, in ArchetypeChunk chunk, in int unfilteredChunkIndex, in int entIndex, in ComponentType currentCompType, in byte* toCopyPtr, in int bufferElementCount, in int compIndex, bool enableBitIsSet)
        {
            if (currentCompType.IsBuffer)
                currentStateSave.SaveBufferForEntityIndex(fullWorldStateSave, entIndex, currentCompType, toCopyPtr, bufferElementCount, enableBitIsSet);
            else
                currentStateSave.SaveCompForEntityIndex(entIndex: entIndex, componentType: currentCompType, toCopyPtr, enableBitIsSet);
        }

        public void UpdateTypesToTrack(ref NativeHashSet<ComponentType> requiredTypes, ref NativeHashSet<ComponentType> optionalTypes)
        {

        }
    }

    // TODO 考虑将逐 Ghost 的索引 HashMap 移到此处，若登记是可选功能，就不应由 WorldStateSave 统一处理
    internal unsafe struct IndexedByGhostSaveStrategy : IStateSaveStrategy
    {
        [ReadOnly] public ComponentTypeHandle<GhostInstance> ghostInstanceHandle;

        // 该策略必须通过此构造函数取得 Handle，才能按 Ghost ID 建立索引
        public IndexedByGhostSaveStrategy(in ComponentTypeHandle<GhostInstance> handle)
        {
            ghostInstanceHandle = handle;
        }

        public void SaveEntity(ref StateSaveContainer currentStateSave, ref WorldStateSave.WorldSaveParallelWriter fullWorldStateSave, in ArchetypeChunk chunk, in int unfilteredChunkIndex, in int entIndex, in ComponentType currentCompType, in byte* toCopyPtr, in int bufferElementCount, in int compIndex, bool enableBitIsSet)
        {
            if (currentCompType.IsBuffer)
                currentStateSave.SaveBufferForEntityIndex(fullWorldStateSave, entIndex, currentCompType, toCopyPtr, bufferElementCount, enableBitIsSet);
            else
                currentStateSave.SaveCompForEntityIndex(entIndex: entIndex, componentType: currentCompType, toCopyPtr, enableBitIsSet);
            GhostInstance* ghostInstancePtr = (GhostInstance*)chunk.GetRequiredComponentDataPtrRO(ref ghostInstanceHandle);
            var ghostInstance = ghostInstancePtr[entIndex];
            if (compIndex == 0)
            {
                var ghost = new SavedEntityID(ghostInstance);
                fullWorldStateSave.RegisterNewEntity(ghost, currentStateSave, entIndex);
            }
        }

        public void UpdateTypesToTrack(ref NativeHashSet<ComponentType> requiredTypes, ref NativeHashSet<ComponentType> optionalTypes)
        {
            requiredTypes.Add(ComponentType.ReadOnly<GhostInstance>());
        }
    }

    [BurstCompile]
    internal unsafe struct StateSaveJob<T> : IJobChunk where T : IStateSaveStrategy
    {
        [ReadOnly] public DynamicTypeList dynamicTypeList; // 用于依赖管理，内容依次为必需类型和可选类型
        [ReadOnly] public NativeArray<ComponentType> requiredTypes;
        [ReadOnly] public NativeArray<ComponentType> optionalTypes;
        [ReadOnly] public EntityTypeHandle entityType;
        [ReadOnly] public EntityStorageInfoLookup entityStorageInfo;

        // 目标状态保存
        public WorldStateSave.WorldSaveParallelWriter fullWorldStateSave; // 包含所有 Container

        [ReadOnly] public T stateSaveStrategy;

        static readonly ProfilerMarker s_StateSaveJobMarker = new ProfilerMarker("StateSaveJob");
        static readonly ProfilerMarker s_StateSaveJobMarker1 = new ProfilerMarker("StateSaveJob1");
        static readonly ProfilerMarker s_StateSaveJobMarker2 = new ProfilerMarker("StateSaveJob2");
        [BurstCompile]
        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            using var a1 = s_StateSaveJobMarker.Auto();
            s_StateSaveJobMarker1.Begin();
            var entityCountInChunk = chunk.Count;

            // 确定当前 Chunk 中需要保存的组件类型
            using NativeList<int> optionalTypesPresentInChunk = new NativeList<int>(Allocator.Temp);
            using NativeList<ComponentType> allComponentTypesInChunk = new(Allocator.Temp);
            using NativeList<int> indicesInDynamicTypeList = new NativeList<int>(Allocator.Temp);
            allComponentTypesInChunk.AddRange(requiredTypes);
            for (var i = 0; i < requiredTypes.Length; i++)
            {
                indicesInDynamicTypeList.Add(i);
            }

            // 查找可选类型在 dynamicTypeList 中的索引
            // TODO 缓存这些索引
            for (int i = 0; i < optionalTypes.Length; i++)
            {
                var optionalIndex = i + requiredTypes.Length;
                if (chunk.Has(ref dynamicTypeList.AsSpan()[optionalIndex]))
                {
                    optionalTypesPresentInChunk.Add(i);
                    allComponentTypesInChunk.Add(optionalTypes[i]);
                    indicesInDynamicTypeList.Add(optionalIndex);
                }
            }
            s_StateSaveJobMarker1.End();
            s_StateSaveJobMarker2.Begin();
            var currentStateSaveContainer = fullWorldStateSave.m_AllStateSaveContainers[unfilteredChunkIndex];
            s_StateSaveJobMarker2.End();

            // 开始复制数据
            for (int compIndex = 0; compIndex < allComponentTypesInChunk.Length; compIndex++)
            {
                var currentCompType = allComponentTypesInChunk[compIndex];
                currentStateSaveContainer.AddComponentType(compIndex, currentCompType);
                var typeInfo = TypeManager.GetTypeInfo(currentCompType.TypeIndex);
                ref var typeHandle = ref dynamicTypeList.AsSpan()[indicesInDynamicTypeList[compIndex]];
                for (int entIndex = 0; entIndex < entityCountInChunk; entIndex++)
                {
                    var array = chunk.GetEnableableBits(ref typeHandle);
                    var bitArray = new UnsafeBitArray(&array, 2 * sizeof(ulong));
                    var enableBitIsSet = bitArray.IsSet(entIndex);
                    if (currentCompType.IsBuffer)
                    {
                        // 保存当前实体的完整 Buffer
                        var bufferData = chunk.GetUntypedBufferAccessor(ref typeHandle);
                        var bufferPtr = bufferData.GetUnsafeReadOnlyPtrAndLength(entIndex, out var length);
                        stateSaveStrategy.SaveEntity(ref currentStateSaveContainer, ref fullWorldStateSave, chunk, unfilteredChunkIndex, entIndex, currentCompType, (byte*)bufferPtr, length, compIndex, enableBitIsSet);
                    }
                    else
                    {
                        var compSize = typeInfo.SizeInChunk;
                        byte* toCopyPtr = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref dynamicTypeList.AsSpan()[indicesInDynamicTypeList[compIndex]], compSize).GetUnsafeReadOnlyPtr();
                        stateSaveStrategy.SaveEntity(ref currentStateSaveContainer, ref fullWorldStateSave, chunk, unfilteredChunkIndex, entIndex, currentCompType,  toCopyPtr, bufferElementCount: 0, compIndex, enableBitIsSet);
                    }
                }
            }
        }
    }
}
