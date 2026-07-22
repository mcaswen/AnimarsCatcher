using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Core;
using Unity.Entities;
using Unity.NetCode.LowLevel.StateSave;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace Unity.NetCode.HostMigration
{
    /// <summary>
    /// 用于访问 Host Migration System 的 Host Migration 类，
    /// 例如获取 Host Migration Data Blob 并把迁移数据部署到新 World
    /// </summary>
    public static class HostMigrationData
    {
        internal struct Data
        {
            // 标记某种 Ghost 类型是否已扫描仅服务器组件并添加到下方 HashMap
            public NativeList<int> ServerOnlyComponentsFlag;
            // 缓存每种 Ghost 类型中存在的仅服务器组件
            public NativeHashMap<int, NativeList<ComponentType>> ServerOnlyComponentsPerGhostType;
        }

        /// <summary>
        /// 获取 Host Migration System 已采集的 Host Migration 数据
        /// 不限制迁移数据的总大小
        /// </summary>
        /// <param name="fromWorld">保存迁移数据的 World</param>
        /// <param name="toData">复制数据的目标列表，容量不足以保存全部数据时会自动调整大小</param>
        public static void Get(World fromWorld, ref NativeList<byte> toData)
        {
            var hostMigrationDataQuery = fromWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<HostMigrationStorage>());
            var hostMigrationData = hostMigrationDataQuery.GetSingletonRW<HostMigrationStorage>();
            var hostData = hostMigrationData.ValueRO.HostDataBlob;
            var ghostData = hostMigrationData.ValueRO.GhostDataBlob;

            var compressedGhostData = CompressGhostDataIfEnabled(fromWorld, ghostData, hostData, out var size);

            if (toData.Capacity < size)
                toData.Resize(size*2, NativeArrayOptions.ClearMemory);

            // 把大小精确设为待复制数据的大小
            toData.Length = size;
            var dataArray = toData.AsArray();
            CopyMigrationData(ref dataArray, hostData, compressedGhostData);
        }

        static unsafe void CopyMigrationData(ref NativeArray<byte> destinationBuffer, NativeList<byte> hostData, NativeList<byte> ghostData)
        {
            // 把 Host Data 大小和 Host Data 复制到目标 Buffer
            var dataPtr = (IntPtr)destinationBuffer.GetUnsafePtr();
            var offset = 0;
            int* header = (int*)dataPtr;
            *header = hostData.Length;
            offset += sizeof(int);
            UnsafeUtility.MemCpy((void*)(dataPtr + offset), hostData.GetUnsafeReadOnlyPtr(), hostData.Length);

            // 把 Ghost Data 大小和 Ghost Data 复制到目标 Buffer 中 Host Data 之后
            offset += hostData.Length;
            header = (int*)(dataPtr + offset);
            *header = ghostData.Length;
            offset += sizeof(int);
            UnsafeUtility.MemCpy((void*)(dataPtr + offset), ghostData.GetUnsafeReadOnlyPtr(), ghostData.Length);
        }

        static void UpdateStatistics(World world, int updateSize)
        {
            using var statsQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<HostMigrationStats>());
            var stats = statsQuery.GetSingleton<HostMigrationStats>();
            stats.TotalUpdateSize -= stats.UpdateSize;
            stats.UpdateSize = updateSize;
            stats.TotalUpdateSize += stats.UpdateSize;
            world.EntityManager.SetComponentData(statsQuery.GetSingletonEntity(), stats);
        }

        /// <summary>
        /// 如果 Host Migration 配置启用压缩，则压缩 Ghost Data
        /// 由于之前记录的是未压缩大小，还需要更新迁移数据统计大小
        /// </summary>
        static NativeList<byte> CompressGhostDataIfEnabled(World world, NativeList<byte> ghostData, NativeList<byte> hostData, out int size)
        {
            // Host Data、Ghost Data 及各自 Header 所需的总大小
            size = hostData.Length + ghostData.Length + sizeof(int) + sizeof(int);

            // 注意：压缩不兼容 Burst，无法在 Host Migration System 中执行，因此必须在此处理
            var compressedGhostData = new NativeList<byte>(ghostData.Length, Allocator.Temp);
            CompressAndEncodeGhostData(ghostData, compressedGhostData);
            size = hostData.Length + compressedGhostData.Length + sizeof(int) + sizeof(int);

            // 之前记录的是未压缩值，因此需要更新统计信息
            UpdateStatistics(world, size);
            return compressedGhostData;
        }

        /// <summary>
        /// 使用 Brotli 压缩 Ghost Data，并对结果进行 Base64 编码
        /// </summary>
        internal static unsafe void CompressAndEncodeGhostData(NativeList<byte> ghostData, NativeList<byte> compressedGhostData)
        {
            using var outputStream = new MemoryStream();
            using var compressor = new BrotliStream(outputStream, System.IO.Compression.CompressionLevel.Fastest);
            compressor.Write(ghostData.AsArray().AsReadOnlySpan());
            compressor.Flush();
            var compressed = Convert.ToBase64String(outputStream.ToArray());
            var stringBytes = Encoding.UTF8.GetBytes(compressed);

            fixed (byte* stringPtr = stringBytes)
            {
                compressedGhostData.AddRange(stringPtr, stringBytes.Length);
            }
            compressedGhostData.Add(0);
        }

        /// <summary>
        /// 在服务器上检查入站连接是否为已知连接的重连，
        /// 并重新添加它在 Host Migration 前具有的全部组件
        /// 注意这里只重新添加组件，不恢复组件数据
        /// </summary>
        internal static bool HandleReconnection(NativeArray<HostConnectionData> hostMigrationConnections, EntityCommandBuffer commandBuffer, Entity connectionEntity, ConnectionUniqueId uniqueId)
        {
            if (!hostMigrationConnections.IsCreated || hostMigrationConnections.Length == 0)
                return false;
            for (int j = 0; j < hostMigrationConnections.Length; ++j)
            {
                var prevConnectionData = hostMigrationConnections[j];
                if (prevConnectionData.UniqueId == uniqueId.Value)
                {
                    var components = prevConnectionData.Components;
                    if (components.Length == 0)
                        return false;
                    foreach (var component in components)
                    {
                        var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(component.StableHash);
                        commandBuffer.AddComponent(connectionEntity, ComponentType.FromTypeIndex(typeIndex));
                    }
                    return true;
                }
            }
            return false;
        }

        internal static unsafe bool RestoreConnectionComponentData(NativeArray<HostConnectionData> hostMigrationConnections, EntityManager entityManager, Entity connectionEntity, ConnectionUniqueId uniqueId)
        {
            entityManager.CompleteAllTrackedJobs(); // 确保动态组件数据指针安全
            for (int j = 0; j < hostMigrationConnections.Length; ++j)
            {
                var prevConnectionData = hostMigrationConnections[j];
                if (prevConnectionData.UniqueId == uniqueId.Value)
                {
                    var components = hostMigrationConnections[j].Components;
                    foreach (var component in components)
                    {
                        var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(component.StableHash);
                        var componentType = ComponentType.FromTypeIndex(typeIndex);
                        if (componentType.IsZeroSized)
                            continue;
                        var typeInfo = TypeManager.GetTypeInfo(typeIndex);
                        var chunk = entityManager.GetChunk(connectionEntity);
                        var typeHandle = entityManager.GetDynamicComponentTypeHandle(componentType);
                        if (!chunk.Has(ref typeHandle))
                        {
                            Debug.LogError($"Component {componentType} not found on connection with unique ID {prevConnectionData.UniqueId} entity {connectionEntity.ToFixedString()} while trying to migrate connection component data");
                            continue;
                        }
                        var indexInChunk = entityManager.GetStorageInfo(connectionEntity).IndexInChunk;
                        var compSize = typeInfo.SizeInChunk;
                        var offset = indexInChunk * compSize;
                        var compDataPtr = (byte*)chunk
                            .GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandle, compSize)
                            .GetUnsafeReadOnlyPtr() + offset;
                        UnsafeUtility.MemCpy(compDataPtr, component.Data.GetUnsafePtr(), compSize);
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 在指定 World 中部署给定 Host Migration 数据
        /// 数据必须由 <see cref="Get"/> 采集，并包含设置 NetCode 状态所需的全部 Ghost Data
        /// 和 Host 专用配置数据
        /// </summary>
        /// <param name="toWorld">部署迁移数据的目标 World</param>
        /// <param name="fromData">Host Migration System 采集的 Host Migration 数据</param>
        public static unsafe void Set(in NativeArray<byte> fromData, World toWorld)
        {
            // 提取 Host Data 部分
            int hostDataSize = 0;
            UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref hostDataSize), (void*)fromData.GetUnsafePtr(), sizeof(int));
            if (hostDataSize + sizeof(int) > fromData.Length)
            {
                Debug.LogError($"Invalid host migration data: Trying to read {hostDataSize} host data bytes, but buffer only has {fromData.Length - sizeof(int)} bytes left");
                return;
            }
            var hostData = new NativeSlice<byte>(fromData, sizeof(int), hostDataSize);

            // 提取 Ghost Data 部分
            var ghostDataPtr = (IntPtr)fromData.GetUnsafePtr() + sizeof(int) + hostDataSize;
            int ghostDataSize = 0;
            int ghostDataStart = 2 * sizeof(int) + hostDataSize;    // Ghost Data 部分在迁移 Buffer 中的起始位置
            UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref ghostDataSize), (void*)ghostDataPtr, sizeof(int));
            if (ghostDataSize + ghostDataStart > fromData.Length)
            {
                Debug.LogError($"Invalid host migration data: Trying to read {ghostDataSize} ghost data bytes, but buffer only has {fromData.Length - ghostDataStart} bytes left");
                return;
            }
            var ghostData = new NativeSlice<byte>(fromData, 2*sizeof(int) + hostDataSize, ghostDataSize);

            Debug.Log($"Migrating server data, host data size = {hostDataSize}, ghost data size = {ghostDataSize}");
            var hostMigrationDataQuery = toWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<HostMigrationStorage>());
            var hostMigrationData = hostMigrationDataQuery.GetSingletonRW<HostMigrationStorage>();
            hostMigrationData.ValueRW.HostData = DecodeHostData(hostData);

            var config = hostMigrationData.ValueRW.HostData.Config;
            using var configQuery = toWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HostMigrationConfig>());
            toWorld.EntityManager.SetComponentData(configQuery.GetSingletonEntity(), config);
            Debug.Log($"Setting host migration configuration StoreOwnGhosts={config.StoreOwnGhosts} MigrationTimeout={config.MigrationTimeout} ServerUpdateInterval={config.ServerUpdateInterval}");

            hostMigrationData.ValueRW.Ghosts = DecompressAndDecodeGhostData(ghostData);

            // TODO 此操作似乎没有生效
            toWorld.SetTime(new TimeData(hostMigrationData.ValueRO.HostData.ElapsedTime, 0));

            using var networkTimeQuery = toWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkTime>());
            var networkTime = networkTimeQuery.GetSingletonRW<NetworkTime>();
            networkTime.ValueRW.ServerTick = hostMigrationData.ValueRO.HostData.ServerTick;
            networkTime.ValueRW.ElapsedNetworkTime = hostMigrationData.ValueRO.HostData.ElapsedNetworkTime;
            Debug.Log($"Setting server state: ElapsedTime={hostMigrationData.ValueRO.HostData.ElapsedTime} ServerTick={networkTime.ValueRW.ServerTick.TickValue} ElapsedNetworkTime={hostMigrationData.ValueRO.HostData.ElapsedNetworkTime}");


            // 把分配 ID 恢复到原服务器的位置
            // 这样可以确保迁移期间实例化的新 Ghost 不会获得与迁移中 Ghost 相同的 ID
            var spawnedGhostEntityMapQuery = toWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<SpawnedGhostEntityMap>());
            var spawnedGhostEntityMapData = spawnedGhostEntityMapQuery.GetSingletonRW<SpawnedGhostEntityMap>();
            if (spawnedGhostEntityMapData.ValueRW.m_ServerAllocatedGhostIds[0] != 1 || spawnedGhostEntityMapData.ValueRW.m_ServerAllocatedGhostIds[1] != 1)
                Debug.LogError($"GhostIds have been assigned before host migration data has been applied, there could be GhostId collisions. No ghosts should be instantiated before host migration data has been set.");

            spawnedGhostEntityMapData.ValueRW.m_ServerAllocatedGhostIds[0] = hostMigrationData.ValueRO.HostData.NextNewGhostId;
            spawnedGhostEntityMapData.ValueRW.m_ServerAllocatedGhostIds[1] = hostMigrationData.ValueRO.HostData.NextNewPrespawnGhostId;

            var prespawnGhostIdRangeBufferEntityQuery = toWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<PrespawnGhostIdRange>());
            var prespawnGhostIdRangeBufferData = prespawnGhostIdRangeBufferEntityQuery.GetSingletonBuffer<PrespawnGhostIdRange>();

            // 设置 PrespawnGhostIdRanges，使 SubScene 加载时能够把 Ghost ID 与旧服务器匹配
            foreach ( var prespawnGhostIdRange in hostMigrationData.ValueRO.HostData.PrespawnGhostIdRanges )
            {
                prespawnGhostIdRangeBufferData.Add(new PrespawnGhostIdRange() {
                    SubSceneHash = prespawnGhostIdRange.SubSceneHash,
                    FirstGhostId = prespawnGhostIdRange.FirstGhostId,
                    Count = 0,
                    Reserved = 0
                });
            }

            // 迁移当前已连接客户端的 Network ID
            using var migratedNetworkIdsQuery = toWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<MigratedNetworkIdsData>());
            if ( migratedNetworkIdsQuery.TryGetSingletonRW<MigratedNetworkIdsData>(out var migratedNetworkIds) )
            {
                migratedNetworkIds.ValueRW.MigratedNetworkIds.Clear(); // 确保容器为空
                foreach (var c in hostMigrationData.ValueRO.HostData.Connections)
                {
                    migratedNetworkIds.ValueRW.MigratedNetworkIds.Add(c.UniqueId, c.NetworkId);
                }
            }

            // 迁移用于分配 Network ID 的信息，确保新连接正确分配且不会与已迁移 ID 重叠
            using var networkIDAllocationDataQuery = toWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkIDAllocationData>());
            if (networkIDAllocationDataQuery.TryGetSingletonRW<NetworkIDAllocationData>(out var networkIDAllocationData))
            {
                networkIDAllocationData.ValueRW.NumNetworkIds.Value = hostMigrationData.ValueRO.HostData.NumNetworkIds;

                foreach ( var a in hostMigrationData.ValueRO.HostData.FreeNetworkIds )
                {
                    networkIDAllocationData.ValueRW.FreeNetworkIds.Enqueue( a );
                }
            }

            toWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
            bool hasPrespawns = false;
            for (int i = 0; i < hostMigrationData.ValueRO.Ghosts.Ghosts.Length; ++i)
            {
                if (hostMigrationData.ValueRO.Ghosts.Ghosts[i].GhostId < 0)
                {
                    hasPrespawns = true;
                    break;
                }
            }
            if (hasPrespawns)
                toWorld.EntityManager.CreateSingleton<ForcePrespawnListPrefabCreate>();

            // 触发服务器 Host Migration System
            var requestEntity = toWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<HostMigrationRequest>());
            toWorld.EntityManager.SetComponentData(requestEntity, new HostMigrationRequest(){ExpectedPrefabCount = hostMigrationData.ValueRW.Ghosts.GhostPrefabs.Length});
            toWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<HostMigrationInProgress>());
        }

        internal static unsafe GhostStorage DecompressAndDecodeGhostData(NativeSlice<byte> ghostDataBlob)
        {
            var dataPtr = (sbyte*)ghostDataBlob.GetUnsafePtr();
            var decodedBytes = Convert.FromBase64String(new string(dataPtr));

            using var inputStream = new MemoryStream(decodedBytes);
            using var decompressStream = new MemoryStream();
            using var compressionStream = new BrotliStream(inputStream, CompressionMode.Decompress);
            compressionStream.CopyTo(decompressStream);
            compressionStream.Flush();
            var decompressedBytes = new NativeArray<byte>(decompressStream.ToArray(), Allocator.Persistent);

            var reader = new DataStreamReader(decompressedBytes);
            var prefabCount = reader.ReadShort();
            var prefabs = new NativeArray<GhostPrefabData>(prefabCount, Allocator.Persistent);
            for (int i = 0; i < prefabCount; ++i)
            {
                var ghostTypeIndex = reader.ReadShort();
                var ghostTypeHash = reader.ReadInt();
                prefabs[i] = new GhostPrefabData()
                {
                    GhostTypeIndex = ghostTypeIndex,
                    GhostTypeHash = ghostTypeHash
                };
            }
            var ghostCount = reader.ReadShort();
            var ghostData = new GhostStorage()
            {
                GhostPrefabs = prefabs,
                // TODO 释放或复用此 Buffer
                Ghosts = new NativeArray<GhostData>(ghostCount, Allocator.Persistent)
            };
            for (int i = 0; i < ghostCount; ++i)
            {
                var ghostId = reader.ReadInt();
                var spawnTick = reader.ReadUInt();
                var ghostType = reader.ReadShort();
                var componentCount = reader.ReadShort();
                var componentData = new NativeArray<DataComponent>(componentCount, Allocator.Persistent);
                for (int j = 0; j < componentCount; ++j)
                {
                    var stableTypeHash = reader.ReadULong();
                    var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(stableTypeHash);
                    var componentType = ComponentType.FromTypeIndex(typeIndex);
                    byte enabled = 0;
                    if (componentType.IsEnableable)
                        enabled = reader.ReadByte();
                    if (componentType.IsBuffer)
                    {
                        var elementSize = TypeManager.GetTypeInfo(typeIndex).ElementSize;
                        var bufferLength = reader.ReadInt();
                        var data = new NativeArray<byte>(bufferLength * elementSize, Allocator.Persistent);
                        reader.ReadBytes(data);
                        componentData[j] = new DataComponent() { StableHash = stableTypeHash, Length = bufferLength, Enabled = enabled == 1, Data = data };
                    }
                    else
                    {
                        var dataLength = TypeManager.GetTypeInfo(typeIndex).TypeSize;
                        var data = new NativeArray<byte>(dataLength, Allocator.Persistent);
                        reader.ReadBytes(data);
                        componentData[j] = new DataComponent() { StableHash = stableTypeHash, Enabled = enabled == 1, Data = data };
                    }
                }

                var newGhostData = new GhostData()
                {
                    GhostId = ghostId,
                    GhostType = ghostType,
                    DataComponents = componentData
                };
                newGhostData.SpawnTick.SerializedData = spawnTick;
                ghostData.Ghosts[i] = newGhostData;
            }
            return ghostData;
        }

        internal static unsafe HostDataStorage DecodeHostData(NativeSlice<byte> data)
        {
            if (data.Length == 0)
            {
                Debug.LogError("Empty buffer given when decoding host data.");
                return default;
            }

            // TODO 避免此次复制，可以让 Data Reader 直接支持 Slice
            var toArray = new NativeArray<byte>(data.Length, Allocator.Temp);
            UnsafeUtility.MemCpy(toArray.GetUnsafePtr(), data.GetUnsafeReadOnlyPtr(), data.Length);
            var reader = new DataStreamReader(toArray);

            var hostData = new HostDataStorage();
            if (reader.Length == 0) return hostData;
            var connectionCount = reader.ReadShort();
            var connections = new NativeArray<HostConnectionData>(connectionCount, Allocator.Persistent);
            for (int i = 0; i < connectionCount; ++i)
            {
                var uniqueId = reader.ReadUInt();
                var networkId = reader.ReadInt();
                var inGame = reader.ReadByte() == 1;
                var scenesLoadedCount = reader.ReadShort();
                var componentCount = reader.ReadShort();
                var components = new NativeArray<ConnectionComponent>(componentCount, Allocator.Persistent);
                for (int j = 0; j < componentCount; ++j)
                {
                    var stableHash = reader.ReadULong();
                    var dataLength = reader.ReadShort();
                    var componentData = new NativeArray<byte>(dataLength, Allocator.Persistent);
                    reader.ReadBytes(componentData);
                    components[j] = new ConnectionComponent() { StableHash = stableHash, Data = componentData };
                }
                connections[i] = new HostConnectionData()
                {
                    UniqueId = uniqueId,
                    NetworkId = networkId,
                    NetworkStreamInGame = inGame,
                    ScenesLoadedCount = scenesLoadedCount,
                    Components = components
                };
            }
            hostData.Connections = connections;

            var subsceneCount = reader.ReadShort();
            var subscenes = new NativeArray<HostSubSceneData>(subsceneCount, Allocator.Persistent);
            for (int i = 0; i < subsceneCount; ++i)
            {
                subscenes[i] = new HostSubSceneData() { SubSceneGuid = new Hash128(reader.ReadUInt(), reader.ReadUInt(), reader.ReadUInt(), reader.ReadUInt()) };
            }
            hostData.SubScenes = subscenes;

            hostData.Config.StoreOwnGhosts = reader.ReadByte() == 1;
            hostData.Config.MigrationTimeout = reader.ReadFloat();
            hostData.Config.ServerUpdateInterval = reader.ReadFloat();
            hostData.ElapsedTime = reader.ReadDouble();
            hostData.ServerTick.SerializedData = reader.ReadUInt();
            hostData.ElapsedNetworkTime = reader.ReadDouble();
            hostData.NextNewGhostId = reader.ReadInt();
            hostData.NextNewPrespawnGhostId = reader.ReadInt();

            var prespawnGhostIdRangesCount = reader.ReadShort();
            var prespawnGhostIdRanges = new NativeArray<HostPrespawnGhostIdRangeData>(prespawnGhostIdRangesCount, Allocator.Persistent);
            for (int i = 0; i < prespawnGhostIdRangesCount; ++i)
            {
                prespawnGhostIdRanges[i] = new HostPrespawnGhostIdRangeData() { SubSceneHash = reader.ReadULong(), FirstGhostId = reader.ReadInt() };
            }
            hostData.PrespawnGhostIdRanges = prespawnGhostIdRanges;

            hostData.NumNetworkIds = reader.ReadInt();
            int numFreeIds = reader.ReadInt();
            hostData.FreeNetworkIds = new NativeArray<int>(numFreeIds,Allocator.Persistent);
            for ( int i=0; i<numFreeIds; ++i )
            {
                hostData.FreeNetworkIds[i] = reader.ReadInt();
            }

            return hostData;
        }
    }
}
