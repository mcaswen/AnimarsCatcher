using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.NetCode.LowLevel.StateSave;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Scenes;
using Debug = UnityEngine.Debug;
using Hash128 = Unity.Entities.Hash128;

namespace Unity.NetCode.HostMigration
{
    /// <summary>
    /// 启用 Host Migration 功能
    /// 此组件会启用 Host Migration System，是 Host Migration 正常工作的必要条件
    /// </summary>
    public struct EnableHostMigration : IComponentData { }

    /// <summary>
    /// Host Migration 后在新服务器上重新生成 Ghost Entity 时添加的 Tag
    /// </summary>
    public struct IsMigrated : IComponentData { }

    /// <summary>
    /// 此组件在 Host Migration 期间始终存在
    /// 可用于根据 Host Migration 状态决定某些系统或操作是否运行
    /// </summary>
    public struct HostMigrationInProgress : IComponentData { }

    /// <summary>
    /// 标记连接，使迁移的组件数据复制到其组件中
    /// </summary>
    struct MigrateComponents : IComponentData
    {
        public int Step;
    }

    struct HostMigrationStorage : IComponentData
    {
        public HostDataStorage HostData;
        public GhostStorage Ghosts;
        public NativeList<byte> HostDataBlob;
        public NativeList<byte> GhostDataBlob;
    }

    /// <summary>
    /// 使用 HostMigrationData 请求 Host Migration
    /// 系统会等待指定 Entity Scene 加载完成
    /// 此组件存在时表示 Host Migration 仍在进行
    /// </summary>
    struct HostMigrationRequest : IComponentData
    {
        /// <summary>
        /// 接管 Host 职责的新服务器正在加载的 SubScene
        /// 必须完成加载后，Host Migration 才能继续生成 Ghost
        /// </summary>
        public NativeArray<Entity> ServerSubScenes;

        /// <summary>
        /// 加载全部 SubScene 并准备好 Ghost Prefab Collection 后，
        /// 新 Host 应存在的 Ghost Prefab 类型数量
        /// </summary>
        public int ExpectedPrefabCount;
    }

    /// <summary>
    /// 用于调整 Host Migration 功能中部分内部系统行为的配置
    /// </summary>
    public struct HostMigrationConfig : IComponentData
    {
        /// <summary>
        /// 是否保存 Host 上本地客户端拥有的 Ghost
        /// Host Migration 期间原 Host 及其客户端都会离开，
        /// 因此可以不包含这个已离开客户端拥有的 Ghost
        /// </summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool StoreOwnGhosts;

        /// <summary>
        /// 部署 Host Migration 数据允许使用的时间
        /// 主要用于等待 SubScene 和完整 Ghost Prefab 列表加载完成
        /// </summary>
        public float MigrationTimeout;

        /// <summary>
        /// 每次采集准备发送到服务的完整 Host Migration 数据之间的时间间隔
        /// </summary>
        public float ServerUpdateInterval;

        /// <summary>
        /// 返回 Host Migration 的默认配置选项
        /// </summary>
        public static HostMigrationConfig Default = new HostMigrationConfig()
        {
            StoreOwnGhosts = false,
            MigrationTimeout = 30.0f,
            ServerUpdateInterval = 2.0f
        };
    }

    /// <summary>
    /// Host 上正在运行的 Host Migration System 统计信息
    /// </summary>
    public struct HostMigrationStats : IComponentData
    {
        /// <summary>
        /// Host Migration 数据中的 Ghost 数量
        /// </summary>
        public int GhostCount;
        /// <summary>
        /// Host Migration 数据中的 Ghost Prefab 数量
        /// </summary>
        public int PrefabCount;
        /// <summary>
        /// 最近一次序列化 Host Migration Data Blob 的大小
        /// 该 Blob 通过 <see cref="HostMigrationData.Get"/> 访问
        /// </summary>
        public int UpdateSize;
        /// <summary>
        /// Host Migration System 目前累计采集的数据总大小
        /// </summary>
        public int TotalUpdateSize;
        /// <summary>
        /// Host Migration Data Blob 最近一次更新的时间
        /// 通过 <see cref="HostMigrationData.Get"/> 访问
        /// </summary>
        public double LastDataUpdateTime;
    }

    struct HostDataStorage
    {
        public NativeArray<HostConnectionData> Connections;
        public NativeArray<HostSubSceneData> SubScenes;
        public HostMigrationConfig Config;
        public double ElapsedTime;
        public NetworkTick ServerTick;
        public double ElapsedNetworkTime;
        public int NextNewGhostId;
        public int NextNewPrespawnGhostId;
        public NativeArray<HostPrespawnGhostIdRangeData> PrespawnGhostIdRanges;
        public int NumNetworkIds;
        public NativeArray<int> FreeNetworkIds;
    }

    struct GhostStorage
    {
        public NativeArray<GhostData> Ghosts;
        public NativeArray<GhostPrefabData> GhostPrefabs;
    }

    struct GhostPrefabData
    {
        public int GhostTypeIndex;
        public int GhostTypeHash;
    }

    struct GhostData
    {
        // 假定 GhostType GUID 与类型索引匹配，并且必须存在匹配的 GhostCollectionPrefab
        public int GhostType;
        /// <summary>
        /// 此已生成 Ghost 类型的 Ghost ID
        /// </summary>
        public int GhostId;
        /// <summary>
        /// 此 Ghost 的 Spawn Tick
        /// </summary>
        public NetworkTick SpawnTick;
        /// <summary>
        /// 每个 Ghost 组件的组件数据
        /// </summary>
        public NativeArray<DataComponent> DataComponents;
    }

    struct DataComponent
    {
        public ulong StableHash;
        public int Length;
        public bool Enabled;
        public NativeArray<byte> Data;
    }

    struct HostSubSceneData
    {
        public Hash128 SubSceneGuid;
    }

    struct HostPrespawnGhostIdRangeData
    {
        // 此范围所应用的 Scene
        public ulong SubSceneHash;
        // 范围中的第一个 ID
        public int FirstGhostId;
    }

    struct HostConnectionData
    {
        // 注意：Transport 已经交换唯一连接 Token，但目前它属于内部连接数据
        // Transport NetworkConnection 也已有唯一 ConnectionId，但同样属于内部数据
        // 它由 ID 和 Version 组成，因此复用的 ID 0 可能表示 Id=0、Version=2，组合后仍然唯一
        // 此值也可以只是递增整数，与 NetworkId 的唯一区别是整个会话期间绝不复用
        // 但这样似乎会重复保存 Transport 内部已经存在的数据
        public uint UniqueId;               // 用于识别之前拥有过哪些 Ghost 的唯一 ID
        public int NetworkId;               // 存在唯一 Connection ID 后此值并非必要，但可能便于调试
        public bool NetworkStreamInGame;    // 迁移发生时可能处于关闭状态，应恢复到相同状态
        public int ScenesLoadedCount;       // PrespawnSectionAck Buffer 会延续到此计数值
        public NativeArray<ConnectionComponent> Components;
    }

    struct ConnectionComponent
    {
        public ulong StableHash;
        public NativeArray<byte> Data;
    }

    struct ConnectionMap : IComponentData
    {
        public NativeHashMap<uint, int> UniqueIdToPreviousNetworkId;
    }

    /// <summary>
    /// 此系统监控 Host Migration 请求，并使用 HostMigrationData 类中设置的数据执行实际迁移
    /// 它还采集准备发送到 Lobby 的迁移数据，
    /// 发送方会监控数据上的更新时间，以检测新数据何时就绪
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(GhostSendSystem))]  // Send System 为新实例化 Entity 分配 Ghost ID 和 GhostType，Host Migration 数据需要这些值已设置并就绪
    [BurstCompile]
    partial struct ServerHostMigrationSystem : ISystem
    {
        EntityQuery m_InGameQuery;
        EntityQuery m_ConnectionQuery;
        EntityQuery m_SubsceneQuery;

        EntityStorageInfoLookup m_EntityStorageInfo;
        ComponentLookup<NetworkId> m_NetworkIdsLookup;
        ComponentLookup<ConnectionUniqueId> m_UniqueIdsLookup;

        double m_MigrationTime;
        double m_LastServerUpdate;
        NativeHashMap<uint, int> m_NetworkIdMap;
        NativeArray<ComponentType> m_DefaultComponents;
        HostMigrationData.Data m_HostMigrationCache;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<HostMigrationConfig>();
            m_HostMigrationCache = new HostMigrationData.Data();
            m_HostMigrationCache.ServerOnlyComponentsFlag = new NativeList<int>(64, Allocator.Persistent);
            m_HostMigrationCache.ServerOnlyComponentsPerGhostType = new NativeHashMap<int, NativeList<ComponentType>>(64, Allocator.Persistent);

            m_DefaultComponents = new NativeArray<ComponentType>(16, Allocator.Persistent);
            m_DefaultComponents[0] = ComponentType.ReadOnly<NetworkStreamConnection>();
            m_DefaultComponents[1] = ComponentType.ReadOnly<CommandTarget>();
            m_DefaultComponents[2] = ComponentType.ReadOnly<NetworkId>();
            m_DefaultComponents[3] = ComponentType.ReadOnly<NetworkSnapshotAck>();
            m_DefaultComponents[4] = ComponentType.ReadOnly<LinkedEntityGroup>();
            m_DefaultComponents[5] = ComponentType.ReadOnly<PrespawnSectionAck>();
            m_DefaultComponents[6] = ComponentType.ReadOnly<IncomingCommandDataStreamBuffer>();
            m_DefaultComponents[7] = ComponentType.ReadOnly<OutgoingRpcDataStreamBuffer>();
            m_DefaultComponents[8] = ComponentType.ReadOnly<IncomingRpcDataStreamBuffer>();
            m_DefaultComponents[9] = ComponentType.ReadOnly<NetworkStreamInGame>();
            m_DefaultComponents[10] = ComponentType.ReadOnly<Simulate>();
            m_DefaultComponents[11] = ComponentType.ReadOnly<ConnectionApproved>();
            m_DefaultComponents[12] = ComponentType.ReadOnly<ConnectionUniqueId>();
            m_DefaultComponents[13] = ComponentType.ReadOnly<NetworkStreamIsReconnected>();
            m_DefaultComponents[14] = ComponentType.ReadOnly<IsMigrated>();
            m_DefaultComponents[15] = ComponentType.ReadOnly<EnablePacketLogging>();

            m_LastServerUpdate = 0.0;
            m_MigrationTime = 0.0;
            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<NetworkStreamDriver>();
            state.RequireForUpdate<EnableHostMigration>();

            m_EntityStorageInfo = state.GetEntityStorageInfoLookup();
            m_NetworkIdsLookup = state.GetComponentLookup<NetworkId>();
            m_UniqueIdsLookup = state.GetComponentLookup<ConnectionUniqueId>();

            var builder = new EntityQueryBuilder(Allocator.Temp);
            builder.WithAll<NetworkStreamInGame>();
            m_InGameQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<NetworkId, CommandTarget, NetworkStreamConnection, ConnectionUniqueId>();
            m_ConnectionQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<SceneSectionData>();
            m_SubsceneQuery = state.GetEntityQuery(builder);

            if (!SystemAPI.TryGetSingleton(out HostMigrationConfig _))
            {
                var entityConfig = state.EntityManager.CreateEntity(ComponentType.ReadWrite<HostMigrationConfig>());
                state.EntityManager.SetName(entityConfig,"HostMigrationConfig");

                state.EntityManager.SetComponentData(entityConfig, HostMigrationConfig.Default);
            }

            var statsEntity = state.EntityManager.CreateEntity(ComponentType.ReadOnly<HostMigrationStats>());
            state.EntityManager.SetName(statsEntity, "HostMigrationStats");
            state.EntityManager.CreateSingleton<HostMigrationStorage>();
            var hostMigrationData = SystemAPI.GetSingletonRW<HostMigrationStorage>();
            hostMigrationData.ValueRW.HostDataBlob = new NativeList<byte>(Allocator.Persistent);
            hostMigrationData.ValueRW.GhostDataBlob = new NativeList<byte>(Allocator.Persistent);
        }

        public void OnDestroy(ref SystemState state)
        {
            var hostMigrationData = SystemAPI.GetSingletonRW<HostMigrationStorage>();
            hostMigrationData.ValueRW.HostDataBlob.Dispose();
            hostMigrationData.ValueRW.GhostDataBlob.Dispose();
            for (int i = 0; i < hostMigrationData.ValueRW.HostData.Connections.Length; i++)
            {
                for (int j = 0; j < hostMigrationData.ValueRW.HostData.Connections[i].Components.Length; j++)
                    hostMigrationData.ValueRW.HostData.Connections[i].Components[j].Data.Dispose();
                hostMigrationData.ValueRW.HostData.Connections[i].Components.Dispose();
            }
            hostMigrationData.ValueRW.HostData.Connections.Dispose();
            hostMigrationData.ValueRW.HostData.SubScenes.Dispose();
            hostMigrationData.ValueRW.HostData.PrespawnGhostIdRanges.Dispose();
            hostMigrationData.ValueRW.HostData.FreeNetworkIds.Dispose();
            hostMigrationData.ValueRW.Ghosts.GhostPrefabs.Dispose();
            for (int i = 0; i < hostMigrationData.ValueRW.Ghosts.Ghosts.Length; i++)
                hostMigrationData.ValueRW.Ghosts.Ghosts[i].DataComponents.Dispose();
            hostMigrationData.ValueRW.Ghosts.Ghosts.Dispose();
            m_HostMigrationCache.ServerOnlyComponentsFlag.Dispose();
            foreach (var componentList in m_HostMigrationCache.ServerOnlyComponentsPerGhostType.GetValueArray(Allocator.Temp))
                componentList.Dispose();
            m_HostMigrationCache.ServerOnlyComponentsPerGhostType.Dispose();
            m_NetworkIdMap.Dispose();
            m_DefaultComponents.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            var hostMigrationData = SystemAPI.GetSingleton<HostMigrationStorage>();

            // 接受连接后恢复上一个会话的连接数据
            foreach (var (uniqueId, migrate, entity) in SystemAPI.Query<RefRO<ConnectionUniqueId>, RefRW<MigrateComponents>>().WithEntityAccess())
            {
                // 首先添加组件并触发结构变更
                if (migrate.ValueRW.Step == 0)
                {
                    migrate.ValueRW.Step++;
                    HostMigrationData.HandleReconnection(hostMigrationData.HostData.Connections, commandBuffer, entity, uniqueId.ValueRO);
                }
                // 向连接 Entity 添加组件后，即可复制迁移的连接数据
                else if (migrate.ValueRW.Step == 1)
                {
                    commandBuffer.RemoveComponent<MigrateComponents>(entity);
                    HostMigrationData.RestoreConnectionComponentData(hostMigrationData.HostData.Connections, state.EntityManager, entity, uniqueId.ValueRO);
                }
            }

            var config = SystemAPI.GetSingleton<HostMigrationConfig>();
            if (SystemAPI.TryGetSingleton<HostMigrationRequest>(out var migrationRequest))
            {
                if (m_MigrationTime == 0.0)
                    m_MigrationTime = state.WorldUnmanaged.Time.ElapsedTime + config.MigrationTimeout;
                if (!SystemAPI.HasSingleton<ConnectionMap>())
                {
                    var connectionMapEntity = state.EntityManager.CreateEntity();
                    m_NetworkIdMap = new NativeHashMap<uint, int>(hostMigrationData.HostData.Connections.Length, Allocator.Persistent);
                    foreach (var con in hostMigrationData.HostData.Connections)
                        m_NetworkIdMap.Add(con.UniqueId, con.NetworkId);
                    state.EntityManager.AddComponentData(connectionMapEntity, new ConnectionMap(){UniqueIdToPreviousNetworkId = m_NetworkIdMap});
                }

                // Prespawn List Prefab 创建后立即开始加载 Entity Scene
                if (migrationRequest.ServerSubScenes.Length == 0 && hostMigrationData.HostData.SubScenes.Length > 0)
                {
                    var sceneEntities = new NativeArray<Entity>(hostMigrationData.HostData.SubScenes.Length, Allocator.Persistent);
                    for (int i = 0; i < hostMigrationData.HostData.SubScenes.Length; ++i)
                    {
                        Debug.Log($"[HostMigration] Server world loading {hostMigrationData.HostData.SubScenes[i].SubSceneGuid}");
                        sceneEntities[i] = SceneSystem.LoadSceneAsync(state.WorldUnmanaged, hostMigrationData.HostData.SubScenes[i].SubSceneGuid);
                    }
                    migrationRequest.ServerSubScenes = sceneEntities;
                    SystemAPI.SetSingleton(migrationRequest);
                    return;
                }

                var allLoaded = true;
                for (int i = 0; i < migrationRequest.ServerSubScenes.Length; ++i)
                {
                    allLoaded &= SceneSystem.IsSceneLoaded(state.WorldUnmanaged, migrationRequest.ServerSubScenes[i]);
                }

                var ghostCollection = SystemAPI.GetSingleton<GhostCollection>();
                if (allLoaded && ghostCollection.NumLoadedPrefabs == migrationRequest.ExpectedPrefabCount)
                {
                    Debug.Log($"[HostMigration] Ready to deploy migration data (time: {m_MigrationTime-config.MigrationTimeout-state.WorldUnmanaged.Time.ElapsedTime})");
                    migrationRequest.ServerSubScenes.Dispose();
                    state.EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<HostMigrationRequest>());
                    state.EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<HostMigrationInProgress>());
                    SpawnAllGhosts(ref state, hostMigrationData.Ghosts.Ghosts, hostMigrationData.Ghosts.GhostPrefabs);
                }

                // Host Migration 已超时，即并非所有 SubScene 都已加载完成，或并非所有 Ghost Prefab 都存在
                if (state.WorldUnmanaged.Time.ElapsedTime > m_MigrationTime)
                {
                    var ghostPrefabs = SystemAPI.GetSingletonBuffer<GhostCollectionPrefab>();
                    if (ghostPrefabs.Length == 0)
                        Debug.LogWarning("No ghost prefabs loaded!");

                    state.EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<HostMigrationRequest>());
                    state.EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<HostMigrationInProgress>());
                    if (!allLoaded)
                        Debug.LogError($"Host migration failed. Did not finish loading migrated scenes (subscene count:{hostMigrationData.HostData.SubScenes.Length})");
                    else
                        Debug.LogError($"Host migration failed. Did not load all ghost prefabs (expected {hostMigrationData.Ghosts.GhostPrefabs.Length} but only have {ghostCollection.NumLoadedPrefabs})");
                    if (m_InGameQuery.IsEmpty)
                        Debug.LogError($"No connection with NetworkStreamInGame found, no ghost prefab will be loaded into the ghost collection until that happens.");
                }
            }
            // 仅在没有进行 Host Migration 时执行更新
            else if (m_LastServerUpdate + config.ServerUpdateInterval < state.WorldUnmanaged.Time.ElapsedTime)
            {
                if (SystemAPI.GetSingleton<GhostCollection>().NumLoadedPrefabs == 0)
                    return;
                m_LastServerUpdate = state.WorldUnmanaged.Time.ElapsedTime;
                var networkTime = SystemAPI.GetSingleton<NetworkTime>();
                state.EntityManager.CompleteAllTrackedJobs();
                GetHostConfigurationForSerializer(ref state, hostMigrationData.HostDataBlob, config, networkTime);

                // TODO 寻找更好的方式采集所需 Ghost 组件类型
                // 当前会遍历全部 Ghost 组件并判断 Ghost Prefab 正在使用哪些组件
                // 项目大约有 500 种 Ghost 类型，但受 DynamicTypeList 限制，NetCode 只支持 128 种，
                // 可通过定义提升到 256 种
                var ghostComponentCollection = SystemAPI.GetSingletonBuffer<GhostComponentSerializer.State>();
                var ghostPrefabsBuffer = SystemAPI.GetSingletonBuffer<GhostCollectionPrefab>();
                var ghostTypes = new NativeHashSet<ComponentType>(ghostComponentCollection.Length, Allocator.Temp);
                foreach (var ghostComponent in ghostComponentCollection)
                {
                    foreach (var ghostPrefab in ghostPrefabsBuffer)
                    {
                        foreach (var usedComponent in state.EntityManager.GetComponentTypes(ghostPrefab.GhostPrefab))
                        {
                            if (usedComponent == ghostComponent.ComponentType)
                            {
                                var componentType = ghostComponent.ComponentType;
                                componentType.AccessModeType = ComponentType.AccessMode.ReadOnly;
                                if (ghostTypes.Contains(componentType))
                                    continue;
                                if (componentType.IsBuffer && IsInputBuffer(componentType, ghostComponentCollection))
                                    continue;
                                ghostTypes.Add(componentType);
                            }
                        }
                    }
                }
                var requiredTypes = new NativeHashSet<ComponentType>(1, Allocator.Temp);
                requiredTypes.Add(ComponentType.ReadOnly<GhostInstance>());

                var stateSave = new WorldStateSave(Allocator.Persistent).WithRequiredTypes(requiredTypes).WithOptionalTypes(ghostTypes).Initialize(ref state);
                var stateSaveJob = stateSave.ScheduleStateSaveJob(ref state);

                // TODO 缓存 Prefab 列表，仅在数量变化时更新
                var ghostPrefabs = new NativeArray<GhostPrefabData>(ghostPrefabsBuffer.Length, Allocator.Persistent);
                for (int i = 0; i < ghostPrefabs.Length; ++i)
                {
                    ghostPrefabs[i] = new GhostPrefabData()
                    {
                        GhostTypeIndex = i,
                        GhostTypeHash = ghostPrefabsBuffer[i].GhostType.GetHashCode()
                    };
                }

                // 查找本地客户端的 Network ID
                int localNetworkId = 0;
                if (!config.StoreOwnGhosts)
                {
                    var networkIdQuery = state.GetEntityQuery(ComponentType.ReadOnly<NetworkId>());
                    var networkIds = networkIdQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
                    if (networkIds.Length > 0)
                    {
                        localNetworkId = networkIds[0].Value;
                    }
                }

                var updateJob = new UpdateMigrationStatsJob()
                {
                    StateSave = stateSave,
                    LocalNetworkId = localNetworkId,
                    StoreOwnGhosts = config.StoreOwnGhosts,
                    WriteLocation = 0,
                    GhostPrefabs = ghostPrefabs,
                    OwnerLookup = state.GetComponentLookup<GhostOwner>(),
                    GhostDataBlob = hostMigrationData.GhostDataBlob,
                    Stats = SystemAPI.GetSingletonRW<HostMigrationStats>(),
                    UpdateTime = state.WorldUnmanaged.Time.ElapsedTime,
                    HostDataSize = hostMigrationData.HostDataBlob.Length,
                };
                state.Dependency = updateJob.Schedule(JobHandle.CombineDependencies(state.Dependency, stateSaveJob));
                state.Dependency.Complete();
            }

            // 对新连接检查其之前是否拥有过任何 Ghost
            var connectionEventsForTick = SystemAPI.GetSingleton<NetworkStreamDriver>().ConnectionEventsForTick;
            for (int i = 0; i < connectionEventsForTick.Length; ++i)
            {
                if (connectionEventsForTick[i].State == ConnectionState.State.Connected)
                {
                    var evt = connectionEventsForTick[i];
                    var uniqueIdLookup = SystemAPI.GetComponentLookup<ConnectionUniqueId>();
                    HandleNetworkStreamInGame(hostMigrationData.HostData.Connections, commandBuffer, evt.ConnectionEntity, uniqueIdLookup[evt.ConnectionEntity]);
                }
            }
            commandBuffer.Playback(state.EntityManager);
        }

        // TODO 缓存结果，或寻找更兼容 Burst 的判断方式
        bool IsInputBuffer(ComponentType componentType, DynamicBuffer<GhostComponentSerializer.State> ghostComponentCollection)
        {
            var collectionData = SystemAPI.GetSingleton<GhostComponentSerializerCollectionData>();
            var ghostCollectionComponentIndex = SystemAPI.GetSingletonBuffer<GhostCollectionComponentIndex>();
            bool isInputBuffer = false;
            foreach (var componentIndex in ghostCollectionComponentIndex)
            {
                if (componentIndex.TypeIndex == componentType.TypeIndex)
                {
                    var componentTypeRW = componentType;
                    componentTypeRW.AccessModeType = ComponentType.AccessMode.ReadWrite;
                    foreach (var componentMapping in collectionData.InputComponentBufferMap)
                    {
                        if (componentMapping.Value == componentTypeRW)
                        {
                            isInputBuffer = true;
                            break;
                        }
                    }
                }
            }
            return isInputBuffer;
        }

        NativeList<Entity> SpawnAllGhosts(ref SystemState state, NativeArray<GhostData> ghosts, NativeArray<GhostPrefabData> ghostPrefabs)
        {
            var ghostEntities = new NativeList<Entity>(ghosts.Length, Allocator.Temp);
            // 创建 Ghost Type 映射，以处理 Ghost 类型索引不一致的情况，例如 SubScene 加载顺序发生变化
            var ghostTypeMap = CreateGhostTypeMap(ghostPrefabs);

            // 保存已经使用的 Ghost ID，以便添加全部 Override 组件后把未使用 ID 标记为空闲
            var hostMigrationData = SystemAPI.GetSingleton<HostMigrationStorage>();
            NativeBitArray migratedGhostIds = new NativeBitArray(hostMigrationData.HostData.NextNewGhostId, Allocator.Temp);

            for (int i = 0; i < ghosts.Length; ++i)
            {
                var entity = Entity.Null;
                var ghost = ghosts[i];
                int ghostType = ghostTypeMap[ghost.GhostType];

                if (!PrespawnHelper.IsPrespawnGhostId(ghost.GhostId))
                {
                    if (ghostTypeMap.Count <= ghost.GhostType)
                    {
                        Debug.LogError($"Did not find migrated ghost type {ghost.GhostType} in the current servers ghost type list (count={ghostTypeMap.Count})");
                        return new NativeList<Entity>(0, Allocator.Temp);
                    }
                    var collectionEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
                    var buffer = state.EntityManager.GetBuffer<GhostCollectionPrefab>(collectionEntity);
                    entity = state.EntityManager.Instantiate(buffer[ghostType].GhostPrefab);

                    if ( ghost.GhostId == 0 )
                    {
                        Debug.LogError($"Received a migrated ghost with an id of 0 this should not be possible. GhostIds are assigned by the GhostSendSystem and this should always run before the migration system ensuring all ghosts have a valid id.");
                    }

                    state.EntityManager.AddComponentData(entity, new OverrideGhostData() { GhostId = ghost.GhostId, SpawnTick = ghost.SpawnTick });
                    migratedGhostIds.Set( ghost.GhostId, true );
                    ghostEntities.Add(entity);
                }
                else
                {
                    foreach (var (ghostId, prespawnEntity) in SystemAPI.Query<RefRO<GhostInstance>>().WithAll<PreSpawnedGhostIndex>().WithEntityAccess())
                    {
                        if (ghostId.ValueRO.ghostId == ghost.GhostId)
                        {
                            entity = prespawnEntity;
                            break;
                        }
                    }

                    if ( entity == Entity.Null)
                    {
                        Debug.LogError($"Trying to migrate prespawn entity with id {ghost.GhostId} but it's scene isn't/hasn't been loaded. This is usually caused by unloading/reordering of subscenes before a migration. Currently this is unsupported.");
                    }
                }

                SetGhostComponentData(ref state, entity, ghostType, ghost.DataComponents);
                state.EntityManager.AddComponent<IsMigrated>(entity);
            }

            // 实例化全部迁移 Ghost 并添加 Override 组件后，把未使用 ID 移回空闲列表
            // 这样可以防止 ID 在多次迁移后不断增长
            var spawnedGhostEntityMapData = SystemAPI.GetSingletonRW<SpawnedGhostEntityMap>();

            for (int i = 1; i < hostMigrationData.HostData.NextNewGhostId; ++i) // 从 1 开始，因为 Ghost ID 0 无效
            {
                if (!migratedGhostIds.IsSet(i))
                {
                    spawnedGhostEntityMapData.ValueRW.m_ServerFreeGhostIds.Enqueue(i);
                }
            }

            return ghostEntities;
        }

        NativeHashMap<int, int> CreateGhostTypeMap(NativeArray<GhostPrefabData> ghostData)
        {
            var ghostPrefabs = SystemAPI.GetSingletonBuffer<GhostCollectionPrefab>();
            var ghostTypeMap = new NativeHashMap<int, int>(ghostPrefabs.Length, Allocator.Temp);
            // 遍历全部已注册 Prefab，验证 Ghost Type Hash 是否与迁移 Ghost 类型中的类型索引匹配
            // 这样按索引 X 生成时，可以确定底层实际 Ghost 类型结构体与迁移前相同
            for (int i = 0; i < ghostPrefabs.Length; ++i)
            {
                for (int j = 0; j < ghostData.Length; ++j)
                {
                    var prefab = ghostData[j];
                    if (prefab.GhostTypeHash == ghostPrefabs[i].GhostType.GetHashCode())
                        ghostTypeMap.Add(prefab.GhostTypeIndex, i);
                }
            }
            if (ghostTypeMap.Count != ghostPrefabs.Length)
                Debug.LogError($"Not all ghost type index have a mapping set (found {ghostTypeMap.Count} but expected {ghostPrefabs.Length})");

            return ghostTypeMap;
        }

        unsafe void SetGhostComponentData(ref SystemState state, Entity ghostEntity, int ghostType, NativeArray<DataComponent> componentDatas)
        {
            var chunk = state.EntityManager.GetChunk(ghostEntity);
            var entityStorageInfo = state.GetEntityStorageInfoLookup();
            var indexInChunk = entityStorageInfo[ghostEntity].IndexInChunk;

            // int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
            // for (int comp = 0; comp < numBaseComponents; ++comp)
            foreach (var componentData in componentDatas)
            {
                var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(componentData.StableHash);
                var typeInfo = TypeManager.GetTypeInfo(typeIndex);
                var componentSize = typeInfo.SizeInChunk;
                var componentType = ComponentType.ReadWrite(typeIndex);
                var typeHandle = state.EntityManager.GetDynamicComponentTypeHandle(componentType);
                var ghostDataPtr = componentData.Data.GetUnsafePtr();

                if (!chunk.Has(ref typeHandle))
                {
                    Debug.LogError($"Component {componentType} not found on ghost entity {ghostEntity.ToFixedString()} ghost type {ghostType} while trying to migrate ghost component data");
                    continue;
                }

                if (componentType.IsEnableable)
                    chunk.SetComponentEnabled(ref typeHandle, indexInChunk, componentData.Enabled);

                if (!componentType.IsBuffer)
                {
                    int offset = indexInChunk * componentSize;
                    var compDataPtr = (byte*)chunk
                        .GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandle, componentSize)
                        .GetUnsafeReadOnlyPtr() + offset;
                    UnsafeUtility.MemCpy(compDataPtr, ghostDataPtr, componentSize);
                }
                else
                {
                    // 反序列化 Buffer，新 Ghost 初始时包含 0 个元素
                    var bufferData = chunk.GetUntypedBufferAccessor(ref typeHandle);
                    var length = componentData.Length;
                    if (length > 0)
                    {
                        bufferData.ResizeUninitialized(indexInChunk, length);
                        var bufferPtr = bufferData.GetUnsafePtr(indexInChunk);
                        UnsafeUtility.MemCpy(bufferPtr, ghostDataPtr, length * componentSize);
                    }
                }
            }
        }

        /// <summary>
        /// 采集 Host Migration 使用的 Host Data
        /// 数据始终以精简 JSON 保存
        /// </summary>
        unsafe void GetHostConfigurationForSerializer(ref SystemState state, NativeList<byte> hostDataBlob, HostMigrationConfig config, NetworkTime networkTime)
        {
            m_EntityStorageInfo.Update(ref state);
            m_NetworkIdsLookup.Update(ref state);
            m_UniqueIdsLookup.Update(ref state);

            var conEntities = m_ConnectionQuery.ToEntityArray(Allocator.Temp);
            var migrationData = new HostDataStorage();
            migrationData.Connections = new NativeArray<HostConnectionData>(conEntities.Length, Allocator.Persistent);
            migrationData.ElapsedTime = state.WorldUnmanaged.Time.ElapsedTime;
            migrationData.Config = config;
            migrationData.ServerTick = networkTime.ServerTick;
            migrationData.ElapsedNetworkTime = networkTime.ElapsedNetworkTime;
            for (int i = 0; i < conEntities.Length; ++i)
            {
                var entity = conEntities[i];
                var chunk = state.EntityManager.GetChunk(entity);
                var indexInChunk = m_EntityStorageInfo[entity].IndexInChunk;
                var archetype = chunk.Archetype;
                var componentTypes = archetype.GetComponentTypes(Allocator.Temp);
                var hasInGame = state.EntityManager.HasComponent(entity, ComponentType.ReadOnly<NetworkStreamInGame>());
                var userComponents = new NativeList<ConnectionComponent>(componentTypes.Length, Allocator.Temp);
                for (int j = 0; j < componentTypes.Length; ++j)
                {
                    var componentType = componentTypes[j];
                    var found = false ;
                    for (int k = 0; k < m_DefaultComponents.Length; ++k)
                    {
                        if (m_DefaultComponents[k].TypeIndex.Index == componentType.TypeIndex.Index)
                            found = true;
                    }

                    if (!found)
                    {
                        var typeInfo = TypeManager.GetTypeInfo(componentType.TypeIndex);
                        var typeHandle = state.EntityManager.GetDynamicComponentTypeHandle(componentType);
                        if (!componentType.IsBuffer)
                        {
                            var compSize = typeInfo.SizeInChunk;
                            var connectionComponent = new ConnectionComponent()
                            {
                                StableHash = typeInfo.StableTypeHash,
                                // TODO 清理此次分配
                                Data = new NativeArray<byte>(compSize, Allocator.Persistent)
                            };
                            if (compSize != 0)
                            {
                                int offset = indexInChunk * compSize;
                                var compDataPtr = (byte*)chunk
                                    .GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandle, compSize)
                                    .GetUnsafeReadOnlyPtr() + offset;
                                UnsafeUtility.MemCpy(connectionComponent.Data.GetUnsafePtr(), compDataPtr, compSize);
                            }
                            userComponents.Add(connectionComponent);
                        }
                        else
                        {

                        }
                    }
                }
                var conData = new HostConnectionData()
                {
                    UniqueId = m_UniqueIdsLookup[entity].Value,
                    NetworkId = m_NetworkIdsLookup[entity].Value,
                    NetworkStreamInGame = hasInGame,
                    Components = userComponents.AsArray()
                };
                migrationData.Connections[i] = conData;
            }

            // 采集 Scene Host Data
            var subsceneData = m_SubsceneQuery.ToComponentDataArray<SceneSectionData>(Allocator.Temp);
            migrationData.SubScenes = new NativeArray<HostSubSceneData>(subsceneData.Length, Allocator.Persistent);
            for (int i = 0; i < subsceneData.Length; ++i)
            {
                migrationData.SubScenes[i] = new HostSubSceneData()
                {
                    SubSceneGuid = subsceneData[i].SceneGUID
                };
            }

            // 获取已分配的最大 Ghost ID，并让迁移后的服务器从相同值开始
            // 确保迁移 Ghost 时不会创建 Ghost ID 冲突的新 Ghost
            migrationData.NextNewGhostId = SystemAPI.GetSingleton<SpawnedGhostEntityMap>().m_ServerAllocatedGhostIds[0];
            migrationData.NextNewPrespawnGhostId = SystemAPI.GetSingleton<SpawnedGhostEntityMap>().m_ServerAllocatedGhostIds[1];

            // 采集 Prespawn Ghost ID Range，用于确保 Prespawn 在多次迁移之间获得匹配 ID
            if (SystemAPI.HasSingleton<PrespawnGhostIdRange>())
            {
                var prespawnGhostIdRanges = SystemAPI.GetBuffer<PrespawnGhostIdRange>(SystemAPI.GetSingletonEntity<PrespawnGhostIdRange>());

                migrationData.PrespawnGhostIdRanges = new NativeArray<HostPrespawnGhostIdRangeData>(prespawnGhostIdRanges.Length, Allocator.Persistent);
                for ( int i=0; i< prespawnGhostIdRanges.Length; ++i )
                {
                    migrationData.PrespawnGhostIdRanges[i] = new HostPrespawnGhostIdRangeData()
                    {
                        SubSceneHash = prespawnGhostIdRanges[i].SubSceneHash,
                        FirstGhostId = prespawnGhostIdRanges[i].FirstGhostId
                        // 此处无需复制 Count，ServerPopulatePrespawnedGhostsSystem::AllocatePrespawnGhostRange 会正确地重新赋值
                    };
                }
            }

            migrationData.NumNetworkIds = SystemAPI.GetSingleton<NetworkIDAllocationData>().NumNetworkIds.Value;
            migrationData.FreeNetworkIds.Dispose();
            migrationData.FreeNetworkIds = SystemAPI.GetSingleton<NetworkIDAllocationData>().FreeNetworkIds.ToArray(Allocator.Persistent);

            hostDataBlob.Clear();

            var writer = new DataStreamWriter(1024, Allocator.Temp);
            WriteHostData(ref writer);
            while (writer.HasFailedWrites)
            {
                writer = new DataStreamWriter(2*writer.Capacity, Allocator.Temp);
                WriteHostData(ref writer);
                if (writer.Length > 100_000)
                {
                    Debug.LogError($"Invalid host data, size reached {writer.Length} bytes");
                    break;
                }
            }
            if (hostDataBlob.Length < writer.Length)
                hostDataBlob.ResizeUninitialized(writer.Length);
            hostDataBlob.CopyFrom(writer.AsNativeArray());

            void WriteHostData(ref DataStreamWriter dataStreamWriter)
            {
                dataStreamWriter.WriteShort((short)migrationData.Connections.Length);
                foreach (var connection in migrationData.Connections)
                {
                    dataStreamWriter.WriteUInt(connection.UniqueId);
                    dataStreamWriter.WriteInt(connection.NetworkId);
                    dataStreamWriter.WriteByte(connection.NetworkStreamInGame ? (byte)1 : (byte)0);
                    dataStreamWriter.WriteShort((short)connection.ScenesLoadedCount);
                    dataStreamWriter.WriteShort((short)connection.Components.Length);
                    foreach (var component in connection.Components)
                    {
                        dataStreamWriter.WriteULong(component.StableHash);
                        dataStreamWriter.WriteShort((short)component.Data.Length);
                        dataStreamWriter.WriteBytes(component.Data);
                    }
                }
                dataStreamWriter.WriteShort((short)migrationData.SubScenes.Length);
                foreach (var subscene in migrationData.SubScenes)
                {
                    dataStreamWriter.WriteUInt(subscene.SubSceneGuid.Value.x);
                    dataStreamWriter.WriteUInt(subscene.SubSceneGuid.Value.y);
                    dataStreamWriter.WriteUInt(subscene.SubSceneGuid.Value.z);
                    dataStreamWriter.WriteUInt(subscene.SubSceneGuid.Value.w);
                }
                dataStreamWriter.WriteByte(migrationData.Config.StoreOwnGhosts ? (byte)1 : (byte)0);
                dataStreamWriter.WriteFloat(migrationData.Config.MigrationTimeout);
                dataStreamWriter.WriteFloat(migrationData.Config.ServerUpdateInterval);
                dataStreamWriter.WriteDouble(migrationData.ElapsedTime);
                dataStreamWriter.WriteUInt(migrationData.ServerTick.SerializedData);
                dataStreamWriter.WriteDouble(migrationData.ElapsedNetworkTime);

                dataStreamWriter.WriteInt(migrationData.NextNewGhostId);
                dataStreamWriter.WriteInt(migrationData.NextNewPrespawnGhostId);

                dataStreamWriter.WriteShort((short)migrationData.PrespawnGhostIdRanges.Length);
                foreach (var idData in migrationData.PrespawnGhostIdRanges)
                {
                    dataStreamWriter.WriteULong(idData.SubSceneHash);
                    dataStreamWriter.WriteInt(idData.FirstGhostId);
                }

                dataStreamWriter.WriteInt(migrationData.NumNetworkIds);
                dataStreamWriter.WriteInt(migrationData.FreeNetworkIds.Length);
                foreach ( var fid in migrationData.FreeNetworkIds)
                    dataStreamWriter.WriteInt(fid);
            }
        }


        /// <summary>
        /// 在服务器上检查入站连接是否为已知连接的重连
        /// 如果是，则应立即让它进入游戏，恢复之前的状态
        /// 必须在连接就绪，即完全连接并取得 Network ID 后执行
        /// </summary>
        void HandleNetworkStreamInGame(NativeArray<HostConnectionData> hostMigrationConnections, EntityCommandBuffer commandBuffer, Entity connectionEntity, ConnectionUniqueId uniqueId)
        {
            for (int j = 0; j < hostMigrationConnections.Length; ++j)
            {
                var prevConnectionData = hostMigrationConnections[j];
                if (prevConnectionData.UniqueId == uniqueId.Value)
                {
                    Debug.Log($"[HostMigration] Setting connection back to in game uniqueId:{uniqueId.Value}");
                    if (prevConnectionData.NetworkStreamInGame)
                        commandBuffer.AddComponent(connectionEntity, ComponentType.ReadOnly<NetworkStreamInGame>());
                    return;
                }
            }
        }
    }

    [BurstCompile]
    internal struct UpdateMigrationStatsJob : IJob
    {
        public WorldStateSave StateSave;
        [NativeDisableUnsafePtrRestriction] public RefRW<HostMigrationStats> Stats;
        public NativeList<byte> GhostDataBlob;
        public int WriteLocation;
        public double UpdateTime;
        public int HostDataSize;
        [ReadOnly] public NativeArray<GhostPrefabData> GhostPrefabs;
        public int LocalNetworkId;
        public bool StoreOwnGhosts;
        public ComponentLookup<GhostOwner> OwnerLookup;

        [BurstCompile]
        public void Execute()
        {
            unsafe
            {
                GhostDataBlob.Clear();
                // 使用估算大小的两倍，因为实际大小可能超过估算值
                var requiredSize =2*(StateSave.Size + GhostPrefabs.Length * sizeof(GhostPrefabData) + 2 * sizeof(int));
                if (GhostDataBlob.Capacity < requiredSize)
                    GhostDataBlob.Resize(2*requiredSize, NativeArrayOptions.ClearMemory);
                GhostDataBlob.Length = GhostDataBlob.Capacity;
                var writer = new DataStreamWriter(GhostDataBlob.AsArray());

                while (!WriteAllGhostData(ref writer))
                {
                    GhostDataBlob.Resize(2*GhostDataBlob.Capacity, NativeArrayOptions.ClearMemory);
                    writer = new DataStreamWriter(GhostDataBlob.AsArray());
                }

                GhostDataBlob.Length = writer.Length;

                Stats.ValueRW.PrefabCount = GhostPrefabs.Length;
                Stats.ValueRW.GhostCount = StateSave.EntityCount;
                Stats.ValueRW.LastDataUpdateTime = UpdateTime;
                var updateSize = HostDataSize + GhostDataBlob.Length;
                Stats.ValueRW.UpdateSize = updateSize;
                Stats.ValueRW.TotalUpdateSize += updateSize;
            }
        }

        bool WriteAllGhostData(ref DataStreamWriter writer)
        {
            // 写入 Prefab 数据
            writer.WriteShort((short)GhostPrefabs.Length);
            foreach (var ghostPrefab in GhostPrefabs)
            {
                writer.WriteShort((short)ghostPrefab.GhostTypeIndex);
                writer.WriteInt(ghostPrefab.GhostTypeHash);
            }

            var ghostCountWriter = writer;
            writer.WriteShort(0);
            short ghostCount = 0;
            foreach (var entry in StateSave)
            {
                if (WriteGhost(ref writer, entry))
                    ghostCount++;

                if (writer.HasFailedWrites)
                    return false;
            }

            ghostCountWriter.WriteShort(ghostCount);
            return true;
        }

        unsafe bool WriteGhost(ref DataStreamWriter writer, WorldStateSave.StateSaveEntry entry)
        {
            // TODO 可以增加快速获取这些 bit 的便捷方法
            // 这些信息位于 SavedEntityID 中，但无法从 Entry 取得
            // 首先查找 GhostInstance，以获取 Ghost ID 和类型信息
            var foundGhostInstance = false;
            GhostInstance ghostInstance = default;
            // TODO 可以添加此 API
            //entry.TryGetComponent<GhostInstance>(out ghostInstance);
            foreach (var compData in entry)
            {
                if (compData.Type == ComponentType.ReadOnly<GhostInstance>())
                {
                    compData.ToConcrete(out ghostInstance);
                    if (ghostInstance.ghostId == 0)
                        Debug.LogError($"Trying to send a ghost with an id of 0 this should not be possible. GhostIds are assigned by the GhostSendSystem and this should always run before the migration system ensuring all ghosts have a valid id.");
                    foundGhostInstance = true;
                }

                if (compData.Type == ComponentType.ReadOnly<GhostOwner>())
                {
                    compData.ToConcrete(out GhostOwner ghostOwner);
                    // Host 即将离开会话，因此忽略它拥有的 Ghost
                    if (!StoreOwnGhosts && ghostOwner.NetworkId == LocalNetworkId)
                        return false;
                }

                // 跳过跟踪已加载 Prespawn Scene 的特殊 Entity
                // 必须跳过整个 Entity 而不只是组件，因为它会自动重建
                if (compData.Type == ComponentType.ReadOnly<PrespawnSceneLoaded>())
                    return false;
            }

            if (!foundGhostInstance)
            {
                Debug.LogError($"Failed to find GhostInstance data on entry");
                return false;
            }

            writer.WriteInt(ghostInstance.ghostId);
            writer.WriteUInt(ghostInstance.spawnTick.SerializedData);
            writer.WriteShort((short)ghostInstance.ghostType);
            writer.WriteShort((short)entry.types.Length);
            foreach (var compData in entry)
            {
                var typeInfo = TypeManager.GetTypeInfo(compData.Type.TypeIndex);
                writer.WriteULong(typeInfo.StableTypeHash);
                if (compData.Type.IsEnableable)
                    writer.WriteByte((byte)(compData.Enabled ? 1 : 0));
                if (compData.Type.IsBuffer)
                {
                    writer.WriteInt(compData.Length);
                    var elementSize = TypeManager.GetTypeInfo(compData.Type.TypeIndex).ElementSize;
                    writer.WriteBytes(new Span<byte>(compData.ComponentAdr, compData.Length * elementSize));
                }
                else
                {
                    writer.WriteBytes(new Span<byte>(compData.ComponentAdr, typeInfo.TypeSize));
                }

                if (writer.HasFailedWrites)
                    return false;
            }

            return true;
        }
    }
}
