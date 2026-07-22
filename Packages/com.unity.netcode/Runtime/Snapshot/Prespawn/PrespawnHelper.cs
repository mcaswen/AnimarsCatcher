using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    internal static class PrespawnHelper
    {
        public const uint PrespawnGhostIdBase = 0x80000000;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static public int MakePrespawnGhostId(int ghostId)
        {
            return (int) (PrespawnGhostIdBase | ghostId);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static public bool IsPrespawnGhostId(int ghostId)
        {
            return (ghostId & PrespawnGhostIdBase) != 0;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static public bool IsRuntimeSpawnedGhost(int ghostId)
        {
            return (ghostId & PrespawnGhostIdBase) == 0;
        }

        // 存在包含该 GhostId 的已保留范围时返回其索引，否则返回 -1
        static public int GhostIdRangeIndex(ref this DynamicBuffer<PrespawnGhostIdRange> ranges , long ghostId)
        {
            ghostId &= ~PrespawnHelper.PrespawnGhostIdBase;
            for (int i = 0; i < ranges.Length; ++i)
            {
                if(ranges[i].Reserved != 0 &&
                   ghostId >= ranges[i].FirstGhostId &&
                   ghostId < ranges[i].FirstGhostId + ranges[i].Count)
                    return i;
            }
            return -1;
        }

        static public Entity CreatePrespawnSceneListGhostPrefab(EntityManager entityManager)
        {
            var e = entityManager.CreateEntity();
            entityManager.AddBuffer<PrespawnSceneLoaded>(e);

            // 使用预测 Ghost 模式，以便始终取得最新接收值而无需等待插值延迟
            var config = new GhostPrefabCreation.Config
            {
                Name = "PrespawnSceneList",
                Importance = 1000,
                MaxSendRate = 0,
                SupportedGhostModes = GhostModeMask.Predicted,
                DefaultGhostMode = GhostMode.Predicted,
                OptimizationMode = GhostOptimizationMode.Static,
                UsePreSerialization = false,
            };

            // 需要使用不会与任何已加载 Prefab 冲突的唯一标识
            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, e, config);

            return e;
        }

        public struct GhostIdInterval: IComparable<GhostIdInterval>
        {
            public int Begin;
            public int End;

            public GhostIdInterval(int begin, int end)
            {
                Begin = begin;
                End = end;
            }
            // 对互不重叠的区间使用简化排序
            public int CompareTo(GhostIdInterval other)
            {
                return Begin.CompareTo(other.Begin);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static public void PopulateSceneHashLookupTable(EntityQuery query, EntityManager entityManager, NativeParallelHashMap<int, ulong> hashMap)
        {
            var chunks = query.ToArchetypeChunkArray(Allocator.Temp);
            var sharedComponentType = entityManager.GetDynamicSharedComponentTypeHandle(ComponentType.ReadOnly<SubSceneGhostComponentHash>());
            hashMap.Clear();
            for (int i = 0; i < chunks.Length; ++i)
            {
                var sharedComponentIndex = chunks[i].GetSharedComponentIndex(ref sharedComponentType);
                var sharedComponentValue = entityManager.GetSharedComponent<SubSceneGhostComponentHash>(sharedComponentIndex);
                hashMap.TryAdd(sharedComponentIndex, sharedComponentValue.Value);
            }
        }


        static public void UpdatePrespawnAckSceneMap(ref ConnectionStateData connectionState,
            Entity PrespawnSceneLoadedEntity,
            in BufferLookup<PrespawnSectionAck> prespawnAckFromEntity,
            in BufferLookup<PrespawnSceneLoaded> prespawnSceneLoadedFromEntity)
        {
            var connectionEntity = connectionState.Entity;
            var clientPrespawnSceneMap = connectionState.AckedPrespawnSceneMap;
            var prespawnSceneLoaded = prespawnSceneLoadedFromEntity[PrespawnSceneLoadedEntity];
            ref var newLoadedRanges = ref connectionState.NewLoadedPrespawnRanges;
            newLoadedRanges.Clear();
            if (!prespawnAckFromEntity.HasBuffer(connectionEntity))
            {
                clientPrespawnSceneMap.Clear();
                return;
            }
            var prespawnAck = prespawnAckFromEntity[connectionEntity];
            var newMap = new NativeParallelHashMap<ulong, int>(prespawnAck.Length, Allocator.Temp);
            for (int i = 0; i < prespawnAck.Length; ++i)
            {
                if(!clientPrespawnSceneMap.ContainsKey(prespawnAck[i].SceneHash))
                    newMap.Add(prespawnAck[i].SceneHash, 1);
                else
                    newMap.Add(prespawnAck[i].SceneHash, 0);
            }
            clientPrespawnSceneMap.Clear();
            for (int i = 0; i < prespawnSceneLoaded.Length; ++i)
            {
                if (newMap.TryGetValue(prespawnSceneLoaded[i].SubSceneHash, out var present))
                {
                    clientPrespawnSceneMap.TryAdd(prespawnSceneLoaded[i].SubSceneHash, 1);
                    // 这是首次确认的新场景
                    if(present == 1)
                    {
                        newLoadedRanges.Add(new GhostIdInterval(
                            PrespawnHelper.MakePrespawnGhostId(prespawnSceneLoaded[i].FirstGhostId),
                            PrespawnHelper.MakePrespawnGhostId(prespawnSceneLoaded[i].FirstGhostId + prespawnSceneLoaded[i].PrespawnCount - 1)));
                    }
                }
            }
            newLoadedRanges.Sort();
        }
    }

    internal static class PrespawnSubsceneElementExtensions
    {
        public static int IndexOf(this DynamicBuffer<PrespawnSceneLoaded> subsceneElements, ulong hash)
        {
            for (int i = 0; i < subsceneElements.Length; ++i)
            {
                if (subsceneElements[i].SubSceneHash == hash)
                    return i;
            }

            return -1;
        }

        public static int IndexOf(this DynamicBuffer<PrespawnSectionAck> subsceneElements, ulong hash)
        {
            for (int i = 0; i < subsceneElements.Length; ++i)
            {
                if (subsceneElements[i].SceneHash == hash)
                    return i;
            }

            return -1;
        }
        public static bool RemoveScene(this DynamicBuffer<PrespawnSectionAck> subsceneElements, ulong hash)
        {
            for (int i = 0; i < subsceneElements.Length; ++i)
            {
                if (subsceneElements[i].SceneHash == hash)
                {
                    subsceneElements.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }
    }

}
