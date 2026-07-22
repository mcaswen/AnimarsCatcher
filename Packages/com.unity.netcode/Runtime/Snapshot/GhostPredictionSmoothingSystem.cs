using System;
using Unity.Entities;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Burst;
using Unity.Jobs;
using System.Runtime.InteropServices;
using Unity.Assertions;
using Unity.Burst.Intrinsics;

namespace Unity.NetCode
{

    /// <summary>
    /// <para>用于为特定组件类型注册 <see cref="SmoothingAction"/> 的单例
    /// <see cref="SmoothingAction"/> 通过随时间改变组件值来修正预测错误，可以注册以下两类平滑动作</para>
    /// <para>- 不携带额外参数的平滑动作，参见 <see cref="RegisterSmoothingAction{T}"/></para>
    /// <para>- 将组件数据作为参数的平滑动作，参见 <see cref="RegisterSmoothingAction{T,U}"/></para>
    /// </summary>
    public struct GhostPredictionSmoothing : IComponentData
    {
        internal GhostPredictionSmoothing(NativeParallelHashMap<ComponentType, SmoothingActionState> actions, NativeList<ComponentType> userComp, EntityQuery singletonQuery)
        {
            m_SmoothingActions = actions;
            m_UserSpecifiedComponentData = userComp;
            m_SingletonQuery = singletonQuery;
        }

        /// <summary>
        /// 所有平滑动作都必须使用此签名，并且兼容 Burst
        /// </summary>
        /// <param name="currentData">当前数据</param>
        /// <param name="previousData">上一份数据</param>
        /// <param name="userData">用户数据</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SmoothingActionDelegate(IntPtr currentData, IntPtr previousData, IntPtr userData);

        internal unsafe struct SmoothingActionState
        {
            public int compIndex;
            public int compSize;
            public int serializerIndex;
            public int entityIndex;
            public int userTypeId;
            public int userTypeSize;
            public byte* backupData;
            public PortableFunctionPointer<SmoothingActionDelegate> action;
        }

        NativeList<ComponentType> m_UserSpecifiedComponentData;
        NativeParallelHashMap<ComponentType, SmoothingActionState> m_SmoothingActions;
        EntityQuery m_SingletonQuery;

        /// <summary>
        /// 为指定组件类型注册不携带额外参数的平滑函数
        /// </summary>
        /// <param name="entityManager">目标 World 中的 EntityManager</param>
        /// <param name="action">指向平滑实现方法且兼容 Burst 的函数指针</param>
        /// <typeparam name="T">组件类型，必须实现 IComponentData 接口</typeparam>
        /// <returns>动作注册成功时为 true，发生错误或动作已注册时为 false</returns>
        public bool RegisterSmoothingAction<T>(EntityManager entityManager, PortableFunctionPointer<SmoothingActionDelegate> action) where T : struct, IComponentData
        {
            var type = ComponentType.ReadWrite<T>();
            if (type.IsBuffer)
            {
                UnityEngine.Debug.LogError("Smoothing actions are not supported for buffers");
                return false;
            }
            if (m_SmoothingActions.ContainsKey(type))
            {
                UnityEngine.Debug.LogError($"There is already a action registered for the type {type.ToString()}");
                return false;
            }

            var actionData = new SmoothingActionState
            {
                action = action,
                compIndex = -1,
                compSize = -1,
                serializerIndex = -1,
                entityIndex = -1,
                backupData = null,
                userTypeId = -1,
                userTypeSize = -1
            };

            m_SmoothingActions.Add(type, actionData);
            if (!m_SingletonQuery.HasSingleton<GhostPredictionSmoothingSystem.SmoothingAction>())
            {
                entityManager.CreateEntity(ComponentType.ReadOnly<GhostPredictionSmoothingSystem.SmoothingAction>());
            }
            return true;
        }

        /// <summary>
        /// 注册将用户指定组件数据作为参数的平滑函数
        /// 最多可使用 8 种不同组件数据类型向平滑函数传递数据
        /// 可注册的平滑动作与组件类型组合数量不受限制
        /// </summary>
        /// <param name="entityManager">目标 World 中的 EntityManager</param>
        /// <param name="action">指向平滑实现方法且兼容 Burst 的函数指针</param>
        /// <typeparam name="T">组件类型，必须实现 IComponentData 接口</typeparam>
        /// <typeparam name="U">作为参数传入函数的用户数据类型</typeparam>
        /// <returns>动作注册成功时为 true，发生错误或动作已注册时为 false</returns>
        public bool RegisterSmoothingAction<T, U>(EntityManager entityManager, PortableFunctionPointer<SmoothingActionDelegate> action)
            where T : struct, IComponentData
            where U : struct, IComponentData
        {
            if (!RegisterSmoothingAction<T>(entityManager, action))
                return false;

            var type = ComponentType.ReadWrite<T>();
            var userType = ComponentType.ReadWrite<U>();
            var userTypeId = -1;
            for (int i = 0; i < m_UserSpecifiedComponentData.Length; ++i)
            {
                if (userType == m_UserSpecifiedComponentData[i])
                {
                    userTypeId = i;
                    break;
                }
            }
            if (userTypeId == -1)
            {
                if (m_UserSpecifiedComponentData.Length == 8)
                {
                    UnityEngine.Debug.LogError("There can only be 8 components registered as user data.");

                    m_SmoothingActions.Remove(type);

                    return false;
                }
                m_UserSpecifiedComponentData.Add(userType);
                userTypeId = m_UserSpecifiedComponentData.Length - 1;
            }
            var actionState = m_SmoothingActions[type];
            actionState.userTypeId = userTypeId;
            actionState.userTypeSize = UnsafeUtility.SizeOf<U>();

            m_SmoothingActions[type] = actionState;
            return true;
        }
    }

    /// <summary>
    /// 通过向所有发生预测错误的预测 Ghost 应用注册到 <see cref="GhostPredictionSmoothing"/> 单例的平滑动作
    /// 来修正客户端预测误差的系统
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(GhostPredictionHistorySystem))]
    [BurstCompile]
    public partial struct GhostPredictionSmoothingSystem : ISystem
    {

        EntityQuery m_PredictionQuery;

        NativeList<ComponentType> m_UserSpecifiedComponentData;
        NativeParallelHashMap<ComponentType, GhostPredictionSmoothing.SmoothingActionState> m_SmoothingActions;

        internal struct SmoothingAction : IComponentData {}

        ComponentTypeHandle<GhostInstance> m_GhostComponentHandle;
        ComponentTypeHandle<PredictedGhost> m_PredictedGhostHandle;
        BufferTypeHandle<LinkedEntityGroup> m_LinkedEntityGroupHandle;
        EntityTypeHandle m_EntityTypeHandle;

        BufferLookup<GhostComponentSerializer.State> m_GhostComponentSerializerStateFromEntity;
        BufferLookup<GhostCollectionPrefabSerializer> m_GhostCollectionPrefabSerializerFromEntity;
        BufferLookup<GhostCollectionComponentIndex> m_GhostCollectionComponentIndexFromEntity;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<PredictedGhost, GhostInstance>();
            m_PredictionQuery = state.GetEntityQuery(builder);

            m_UserSpecifiedComponentData = new NativeList<ComponentType>(8, Allocator.Persistent);
            m_SmoothingActions = new NativeParallelHashMap<ComponentType, GhostPredictionSmoothing.SmoothingActionState>(32, Allocator.Persistent);

            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<SmoothingAction>();


            m_GhostComponentHandle = state.GetComponentTypeHandle<GhostInstance>(true);
            m_PredictedGhostHandle = state.GetComponentTypeHandle<PredictedGhost>(true);
            m_LinkedEntityGroupHandle = state.GetBufferTypeHandle<LinkedEntityGroup>(true);
            m_EntityTypeHandle = state.GetEntityTypeHandle();

            m_GhostComponentSerializerStateFromEntity = state.GetBufferLookup<GhostComponentSerializer.State>(true);
            m_GhostCollectionPrefabSerializerFromEntity = state.GetBufferLookup<GhostCollectionPrefabSerializer>(true);
            m_GhostCollectionComponentIndexFromEntity = state.GetBufferLookup<GhostCollectionComponentIndex>(true);

            builder = new EntityQueryBuilder(Allocator.Temp).WithAll<SmoothingAction>();
            var enableQuery = state.GetEntityQuery(builder);
            var atype = new NativeArray<ComponentType>(1, Allocator.Temp);
            atype[0] = ComponentType.ReadWrite<GhostPredictionSmoothing>();
            var smoothingSingleton = state.EntityManager.CreateEntity(state.EntityManager.CreateArchetype(atype));
            FixedString64Bytes singletonName = "GhostPredictionSmoothing-Singleton";
            state.EntityManager.SetName(smoothingSingleton, singletonName);
            SystemAPI.SetSingleton(new GhostPredictionSmoothing(m_SmoothingActions, m_UserSpecifiedComponentData, enableQuery));
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            m_UserSpecifiedComponentData.Dispose();
            m_SmoothingActions.Dispose();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var newtorkTime = SystemAPI.GetSingleton<NetworkTime>();
            var lastBackupTime = SystemAPI.GetSingleton<GhostSnapshotLastBackupTick>();

            if (newtorkTime.ServerTick != lastBackupTime.Value)
                return;

            if (m_SmoothingActions.IsEmpty)
            {
                state.EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<SmoothingAction>());
                return;
            }


            m_GhostComponentHandle.Update(ref state);
            m_PredictedGhostHandle.Update(ref state);
            m_LinkedEntityGroupHandle.Update(ref state);
            m_EntityTypeHandle.Update(ref state);

            m_GhostComponentSerializerStateFromEntity.Update(ref state);
            m_GhostCollectionPrefabSerializerFromEntity.Update(ref state);
            m_GhostCollectionComponentIndexFromEntity.Update(ref state);
            var smoothingJob = new PredictionSmoothingJob
            {
                predictionState = SystemAPI.GetSingleton<GhostPredictionHistoryState>().PredictionState,
                ghostType = m_GhostComponentHandle,
                predictedGhostType = m_PredictedGhostHandle,
                entityType = m_EntityTypeHandle,

                GhostCollectionSingleton = SystemAPI.GetSingletonEntity<GhostCollection>(),
                GhostComponentCollectionFromEntity = m_GhostComponentSerializerStateFromEntity,
                GhostTypeCollectionFromEntity = m_GhostCollectionPrefabSerializerFromEntity,
                GhostComponentIndexFromEntity = m_GhostCollectionComponentIndexFromEntity,

                childEntityLookup = state.GetEntityStorageInfoLookup(),
                linkedEntityGroupType = m_LinkedEntityGroupHandle,
                tick = newtorkTime.ServerTick,

                smoothingActions = m_SmoothingActions
            };

            var ghostComponentCollection = state.EntityManager.GetBuffer<GhostCollectionComponentType>(smoothingJob.GhostCollectionSingleton);
            DynamicTypeList.PopulateList(ref state, ghostComponentCollection, false, ref smoothingJob.DynamicTypeList);
            DynamicTypeList.PopulateListFromArray(ref state, m_UserSpecifiedComponentData.AsArray(), true, ref smoothingJob.UserList);

            state.Dependency = smoothingJob.ScheduleParallelByRef(m_PredictionQuery, state.Dependency);
        }

        [BurstCompile]
        struct PredictionSmoothingJob : IJobChunk
        {
            public DynamicTypeList DynamicTypeList;
            public DynamicTypeList UserList;
            public NativeParallelHashMap<ArchetypeChunk, System.IntPtr>.ReadOnly predictionState;

            [ReadOnly] public ComponentTypeHandle<GhostInstance> ghostType;
            [ReadOnly] public ComponentTypeHandle<PredictedGhost> predictedGhostType;
            [ReadOnly] public EntityTypeHandle entityType;

            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferLookup<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;

            [ReadOnly] public EntityStorageInfoLookup childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;

            [ReadOnly] public NativeParallelHashMap<ComponentType, GhostPredictionSmoothing.SmoothingActionState> smoothingActions;
            public NetworkTick tick;

            const GhostSendType requiredSendMask = GhostSendType.OnlyPredictedClients;

            public unsafe void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // 此 Job 不支持包含可启用组件类型的查询
                Assert.IsFalse(useEnabledMask);

                if (!predictionState.TryGetValue(chunk, out var state) ||
                    (*(PredictionBackupState*)state).entityCapacity != chunk.Capacity)
                    return;

                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr = DynamicTypeList.GetData();
                int ghostChunkComponentTypesLength = DynamicTypeList.Length;
                DynamicComponentTypeHandle* userTypes = UserList.GetData();
                int userTypesLength = UserList.Length;

                var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                var GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];
                var GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];

                var ghostComponents = chunk.GetNativeArray(ref ghostType);

                int ghostTypeId = ghostComponents.GetFirstGhostTypeId();
                if (ghostTypeId < 0)
                    return;
                if (ghostTypeId >= GhostTypeCollection.Length)
                    return; // 序列化数据尚未加载，这只会发生在预生成对象上

                var typeData = GhostTypeCollection[ghostTypeId];
                Entity* backupEntities = PredictionBackupState.GetEntities(state);
                var entities = chunk.GetNativeArray(entityType);

                var PredictedGhosts = chunk.GetNativeArray(ref predictedGhostType);

                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                var actions = new NativeList<GhostPredictionSmoothing.SmoothingActionState>(Allocator.Temp);
                var childActions = new NativeList<GhostPredictionSmoothing.SmoothingActionState>(Allocator.Temp);

                byte* dataPtr = PredictionBackupState.GetData(state);
                // TODO：可以按 Chunk Capacity 缓存此循环结果，当前每次都会重新计算
                for (int comp = 0; comp < typeData.NumComponents; ++comp)
                {
                    int index = typeData.FirstComponent + comp;
                    int compIdx = GhostComponentIndex[index].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[index].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (compIdx >= ghostChunkComponentTypesLength)
                        throw new System.InvalidOperationException("Component index out of range");
#endif
                    if ((GhostComponentIndex[index].SendMask&requiredSendMask) == 0)
                        continue;

                    // Buffer 不支持平滑动作
                    if (GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                    {
                        dataPtr = PredictionBackupState.GetNextData(dataPtr, GhostComponentSerializer.DynamicBufferComponentSnapshotSize, chunk.Capacity);
                        continue;
                    }
                    var compSize = GhostComponentCollection[serializerIdx].ComponentSize;
                    if (smoothingActions.TryGetValue(GhostComponentCollection[serializerIdx].ComponentType, out var action))
                    {
                        action.compIndex = compIdx;
                        action.compSize = compSize;
                        action.serializerIndex = serializerIdx;
                        action.entityIndex = GhostComponentIndex[index].EntityIndex;
                        action.backupData = dataPtr;

                        if (comp < numBaseComponents)
                            actions.Add(action);
                        else
                            childActions.Add(action);
                    }
                    dataPtr = PredictionBackupState.GetNextData(dataPtr, compSize, chunk.Capacity);
                }

                foreach (var action in actions)
                {
                    if (chunk.Has(ref ghostChunkComponentTypesPtr[action.compIndex]))
                    {
                        for (int ent = 0; ent < entities.Length; ++ent)
                        {
                            // 如果此实体未执行任何预测，就不会发生回滚，也无需进行平滑
                            if (!PredictedGhosts[ent].ShouldPredict(tick))
                                continue;

                            if (entities[ent] != backupEntities[ent])
                                continue;

                            var compData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostChunkComponentTypesPtr[action.compIndex], action.compSize).GetUnsafePtr();

                            void* usrDataPtr = null;
                            if (action.userTypeId >= 0 && chunk.Has(ref userTypes[action.userTypeId]))
                            {
                                var usrData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref userTypes[action.userTypeId], action.userTypeSize).GetUnsafeReadOnlyPtr();
                                usrDataPtr = usrData + action.userTypeSize * ent;
                            }

                            action.action.Ptr.Invoke((IntPtr)(compData + action.compSize * ent), (IntPtr)(action.backupData + action.compSize * ent),
                                (IntPtr)usrDataPtr);
                        }
                    }
                }

                var linkedEntityGroupAccessor = chunk.GetBufferAccessor(ref linkedEntityGroupType);
                foreach (var action in childActions)
                {
                    for (int ent = 0, chunkEntityCount = chunk.Count; ent < chunkEntityCount; ++ent)
                    {
                        // 如果此实体未执行任何预测，就不会发生回滚，也无需进行平滑
                        if (!PredictedGhosts[ent].ShouldPredict(tick))
                            continue;
                        if (entities[ent] != backupEntities[ent])
                            continue;
                        var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                        var childEnt = linkedEntityGroup[action.entityIndex].Value;
                        if (childEntityLookup.TryGetValue(childEnt, out var childChunk) &&
                            childChunk.Chunk.Has(ref ghostChunkComponentTypesPtr[action.compIndex]))
                        {
                            var compData = (byte*)childChunk.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostChunkComponentTypesPtr[action.compIndex], action.compSize).GetUnsafePtr();

                            void* usrDataPtr = null;
                            if (action.userTypeId >= 0 && chunk.Has(ref userTypes[action.userTypeId]))
                            {
                                var usrData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref userTypes[action.userTypeId], action.userTypeSize).GetUnsafeReadOnlyPtr();
                                usrDataPtr = usrData + action.userTypeSize * ent;
                            }
                            action.action.Ptr.Invoke((IntPtr)(compData + action.compSize * childChunk.IndexInChunk), (IntPtr)(action.backupData + action.compSize * ent), (IntPtr)usrDataPtr);
                        }
                    }
                }
            }
        }
    }
}
