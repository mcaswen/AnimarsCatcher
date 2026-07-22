#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
#if ENTITIES_1_5_OR_NEWER
// 取消此行注释可测试使用反射的版本，仅用于测试
#define  HAS_NEW_SYSTEMATTRIBUTE_API
#endif

using Unity.Entities;
using System;
using Unity.Core;
using Unity.Collections;
using Unity.Physics;
using Unity.Physics.Extensions;
using Unity.Physics.GraphicsIntegration;
using Unity.Physics.Systems;
using Unity.Transforms;
using System.Collections.Generic;
using System.Reflection;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    /// 控制 Physics 模拟何时运行的 Rate Manager
    /// 需要处理以下使用场景
    /// <para>
    /// 在服务器上
    /// <list>
    /// <li>是否要求存在 Physics 对象：否，即使所有 Physics 内容都已移除，也应始终运行以重建空 World</li>
    /// <li>静态 Physics：是，可能需要进行 Raycast</li>
    /// <li>动态 Physics：是，即使未复制也需要</li>
    /// <li>静态或动态 Trigger：是</li>
    /// <li>带 Physics 的非 Ghost Kinematic：是</li>
    /// <li>带 Physics 的 Predicted Ghost：是</li>
    /// <li>带 Physics 的插值 Ghost：是，作为 Kinematic 处理</li>
    /// <li>启用延迟补偿：是，需要重建碰撞历史</li>
    /// </list>
    /// </para>
    /// <para>
    /// 在客户端上
    /// <list type="">
    /// <li>是否要求存在 Physics 对象：理想情况下是，实际则否，即使所有 Physics 内容都已移除，也应始终运行以重建空 World</li>
    /// <li>静态 Physics：是，可能需要进行 Raycast；没有 Ghost 时，理想做法是使用仅客户端 Physics，由用户决定</li>
    /// <li>动态 Physics：是，即使未复制也需要；此时将其保留在 World 0 并不理想但确有必要，由用户决定</li>
    /// <li>带 Physics 的非 Ghost Kinematic：是；此时将其保留在 World 0 并不理想但确有必要，由用户决定</li>
    /// <li>带 Physics 的 Predicted Ghost：是</li>
    /// <li>带 Physics 的插值 Ghost：是，作为 Kinematic 处理；此时预测应只运行一次，但应由用户决定，而不是采用隐含的固定默认值</li>
    /// <li>启用延迟补偿：是</li>
    ///</list>
    /// 总体而言，该 Group 默认应始终运行，但这会造成破坏性变更，
    /// 因此需要通过 <see cref="PhysicGroupRunMode"/> 枚举显式启用此行为
    /// </summary>
    class NetcodePhysicsRateManager : IRateManager
    {
        private bool m_DidUpdate;
        private EntityQuery m_LagCompensationQuery;
        private EntityQuery m_predictedPhysicsQuery;
        private EntityQuery m_relaxedPhysicsQuery;
        private EntityQuery m_PhysicsGroupConfigQuery;
        private EntityQuery m_NetworkTimeQuery;
        public NetcodePhysicsRateManager(ComponentSystemGroup group)
        {
            var queryBuilder = new EntityQueryBuilder(Allocator.Temp);
            // 当前默认行为：只要存在带 PhysicsVelocity 的 Entity，无论是 Kinematic 还是 Dynamic，都允许 Physics 运行
            // 这是非常严格的条件，尤其是在客户端上
            // 服务器也可能不适合此条件，例如可能仍需对某些几何体执行 Raycast
            queryBuilder.WithAll<PredictedGhost>().WithAny<PhysicsVelocity>();
            m_predictedPhysicsQuery = queryBuilder.Build(group.EntityManager);
            // 这是更宽松的条件，只要存在某些 Ghost Physics Entity 就允许运行 Physics
            // 该行为更合理，但会打破原始默认值的一些假设和行为，因此只作为可选项提供
            // 如果所有 Physics Entity 都已销毁，此模式仍无法正确工作，因为 Physics Collision World 会变得陈旧
            // 但启用延迟补偿后可以正常工作
            queryBuilder.Reset();
            queryBuilder.WithAny<PhysicsVelocity, PhysicsCollider>();
            m_relaxedPhysicsQuery = queryBuilder.Build(group.EntityManager);
            m_LagCompensationQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<LagCompensationConfig>());
            m_PhysicsGroupConfigQuery = group.World.EntityManager.CreateEntityQuery(typeof(PhysicsGroupConfig));
            m_NetworkTimeQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
        }
        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            if (m_DidUpdate)
            {
                m_DidUpdate = false;
                return false;
            }
            m_PhysicsGroupConfigQuery.TryGetSingleton(out PhysicsGroupConfig groupConfig);
            if (groupConfig.PhysicsRunMode != PhysicGroupRunMode.AlwaysRun)
            {
                bool noEntitiesMatchingQuery;
                if (groupConfig.PhysicsRunMode == PhysicGroupRunMode.LagCompensationEnabledOrKinematicGhosts)
                    noEntitiesMatchingQuery = m_predictedPhysicsQuery.IsEmptyIgnoreFilter;
                else
                    noEntitiesMatchingQuery = m_relaxedPhysicsQuery.IsEmptyIgnoreFilter;

                // 查询为空且未启用延迟补偿时，无需运行
                if (noEntitiesMatchingQuery)
                {
                    // 在客户端上，用户将此值设为 0 等同于禁用历史备份
                    if (m_LagCompensationQuery.IsEmptyIgnoreFilter ||
                        (group.World.IsClient() &&
                         m_LagCompensationQuery.GetSingleton<LagCompensationConfig>().ClientHistorySize == 0))
                    {
                        return false;
                    }
                    // 启用延迟补偿后，仅在新的完整 Tick 上运行
                    var netTime = m_NetworkTimeQuery.GetSingleton<NetworkTime>();
                    if (!netTime.IsFirstTimeFullyPredictingTick)
                        return false;
                }
            }
            m_DidUpdate = true;
            return true;
        }
        public float Timestep
        {
            get
            {
                throw new System.NotImplementedException();
            }
            set
            {
                throw new System.NotImplementedException();
            }
        }
    }

    static class MovePhysicsSystemUtilities
    {
#if !HAS_NEW_SYSTEMATTRIBUTE_API
        // TODO：新版 Entities Package 公开后移除此变通方案
        private static MethodInfo s_HackGetSystemTypeMethod = null;
        private static int s_FieldOffset = -1;
        public static Type GetSystemType(World world, SystemHandle systemHandle)
        {
            if (s_HackGetSystemTypeMethod == null || s_FieldOffset == -1)
            {
                s_HackGetSystemTypeMethod = typeof(TypeManager).GetMethod("GetSystemType",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                    null, new Type[] { typeof(SystemTypeIndex) }, null);
                Assertions.Assert.IsNotNull(s_HackGetSystemTypeMethod);
                var fieldInfo = typeof(SystemState).GetField("m_SystemTypeIndex",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assertions.Assert.IsNotNull(fieldInfo);
                s_FieldOffset = UnsafeUtility.GetFieldOffset(fieldInfo);
            }

            ref var systemState = ref world.Unmanaged.ResolveSystemStateRef(systemHandle);
            SystemTypeIndex systemTypeIndex;
            unsafe { fixed (void* data = &systemState) {
                systemTypeIndex = *(SystemTypeIndex*)((byte*)data + s_FieldOffset);
            }}
            return (Type)s_HackGetSystemTypeMethod.Invoke(null, new object[] { systemTypeIndex });
        }

        public static bool MovePhysicsSystem(Type systemType, SystemHandle handle,
            ref NativeHashMap<SystemTypeIndex, SystemHandle> physicsSystemTypes)
        {
            SystemTypeIndex systemTypeIndex = TypeManager.GetSystemTypeIndex(systemType);
            if (physicsSystemTypes.ContainsKey(systemTypeIndex))
                return false;
            var attribs = TypeManager.GetSystemAttributes(systemType, typeof(UpdateBeforeAttribute));
            foreach (var attr in attribs)
            {
                var dependencyTypeIndex = TypeManager.GetSystemTypeIndex(((UpdateBeforeAttribute)attr).SystemType);
                if (physicsSystemTypes.ContainsKey(dependencyTypeIndex))
                {
                    physicsSystemTypes[systemTypeIndex] = handle;
                    return true;
                }
            }
            attribs = TypeManager.GetSystemAttributes(systemType, typeof(UpdateAfterAttribute));
            foreach (var attr in attribs)
            {
                var dependencyTypeIndex = TypeManager.GetSystemTypeIndex(((UpdateAfterAttribute)attr).SystemType);
                if (physicsSystemTypes.ContainsKey(dependencyTypeIndex))
                {
                    physicsSystemTypes[systemTypeIndex] = handle;
                    return true;
                }
            }
            return false;
        }

        public static void MovePhysicsSystems(FixedStepSimulationSystemGroup srcGrp,
            PredictedFixedStepSimulationSystemGroup dstGrp, ref NativeHashMap<SystemTypeIndex, SystemHandle> physicsSystemTypes)
        {
            bool didMove = true;
            var managedSystems = srcGrp.ManagedSystems;
            var unmanagedSystems = srcGrp.GetUnmanagedSystems();
            while (didMove)
            {
                didMove = false;
                foreach (var system in managedSystems)
                {
                    didMove |= MovePhysicsSystemUtilities.MovePhysicsSystem(system.GetType(), system.SystemHandle, ref physicsSystemTypes);
                }
                foreach (var system in unmanagedSystems)
                {
                    var systemType = MovePhysicsSystemUtilities.GetSystemType(srcGrp.World, system);
                    didMove |= MovePhysicsSystemUtilities.MovePhysicsSystem(systemType, system, ref physicsSystemTypes);
                }
            }
        }
#else
        public static void MovePhysicsSystems(FixedStepSimulationSystemGroup srcGrp,
            PredictedFixedStepSimulationSystemGroup dstGrp,
            ref NativeHashMap<SystemTypeIndex, SystemHandle> physicsSystemTypes)
        {
            bool didMove = true;
            var systems = srcGrp.GetAllSystems();
            while (didMove)
            {
                didMove = false;
                foreach (var system in systems)
                {
                    var systemTypeIndex = srcGrp.World.Unmanaged.GetSystemTypeIndex(system);
                    didMove |= MovePhysicsSystemUtilities.MovePhysicsSystem(system, systemTypeIndex, ref physicsSystemTypes);
                }
            }
        }

        public static bool MovePhysicsSystem(SystemHandle handle, SystemTypeIndex systemTypeIndex,
            ref NativeHashMap<SystemTypeIndex, SystemHandle> physicsSystemTypes)
        {
            if (physicsSystemTypes.ContainsKey(systemTypeIndex))
                return false;
            var attribs = TypeManager.GetSystemAttributes(systemTypeIndex, TypeManager.SystemAttributeKind.UpdateBefore);
            foreach (var attr in attribs)
            {
                if (physicsSystemTypes.ContainsKey(attr.TargetSystemTypeIndex))
                {
                    physicsSystemTypes[systemTypeIndex] = handle;
                    return true;
                }
            }
            attribs = TypeManager.GetSystemAttributes(systemTypeIndex, TypeManager.SystemAttributeKind.UpdateAfter);
            foreach (var attr in attribs)
            {
                if (physicsSystemTypes.ContainsKey(attr.TargetSystemTypeIndex))
                {
                    physicsSystemTypes[systemTypeIndex] = handle;
                    return true;
                }
            }
            return false;
        }
#endif
    }

    /// <summary>
    /// 为预测配置 Physics 的系统
    /// 它会将 PhysicsSystemGroup 移入 PredictedFixedStepSimulationSystemGroup
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class PredictedPhysicsConfigSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            MovePhysicsSystems();
            var physGrp = World.GetExistingSystemManaged<PhysicsSystemGroup>();
            physGrp.RateManager = new NetcodePhysicsRateManager(physGrp);
            World.GetExistingSystemManaged<InitializationSystemGroup>().RemoveSystemFromUpdateList(this);
        }

        void MovePhysicsSystems()
        {
            var srcGrp = World.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
            var dstGrp = World.GetExistingSystemManaged<PredictedFixedStepSimulationSystemGroup>();

            var physicsSystemTypes = new NativeHashMap<SystemTypeIndex, SystemHandle>(100, Allocator.Temp);
            var physicsGroupTypeIndex = TypeManager.GetSystemTypeIndex<PhysicsSystemGroup>();
            // TODO：增量构建 Physics World 和多 World 模式总体存在以下问题
            // - InjectTemporalInfo 相关系统会忽略 PhysicsWorld 索引，为所有 Physics Entity 添加时间一致性数据
            // - InjectTemporalInfo 没有被移动，虽然存在一个最后兜底的系统执行此操作，但这种做法似乎并不正确
            // - 目前还无法为每个 PhysicsWorld 获取独立的 PhysicsStep，理想情况下应能为不同 Physics World 指定不同设置和模拟频率
            // TODO：从源头解决问题，Physics 和 Fixed Step Group 本就不应被移动
            //       这意味着需要修改整个 System Group 及其顺序，可在 2.0 中进一步设计
            physicsSystemTypes.Add(physicsGroupTypeIndex, srcGrp.World.GetExistingSystem(physicsGroupTypeIndex));
            MovePhysicsSystemUtilities.MovePhysicsSystems(srcGrp, dstGrp, ref physicsSystemTypes);
            foreach (var kv in physicsSystemTypes)
            {
                // TODO：此处处理很繁琐，Group API 应提供更一致的接口
                if (!kv.Key.IsManaged)
                    srcGrp.RemoveSystemFromUpdateList(kv.Value);
                else
                    srcGrp.RemoveSystemFromUpdateList(World.GetExistingSystemManaged(kv.Key));
                dstGrp.AddSystemToUpdateList(kv.Value);
            }
        }
    }

    /// <summary>
    /// 如果 World 中存在此类型的 Singleton，客户端默认 Physics World 中所有带动态 Physics 的非 Ghost
    /// 都会被移到指定的 Physics World 索引
    /// 这是因为预测 Physics 循环无法处理不参与回滚的对象
    /// </summary>
    public struct PredictedPhysicsNonGhostWorld : IComponentData
    {
        /// <summary>
        /// Entity 要移入的 Physics World 索引
        /// </summary>
        public uint Value;
    }

    /// <summary>
    /// 用于检测客户端预测 Physics World 中无效动态 Physics 对象的系统
    /// 如果 PredictedPhysicsNonGhostWorld 存在且不为 0，此系统还会将 Entity 移到正确的 World
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [BurstCompile]
    public partial struct PredictedPhysicsValidationSystem : ISystem
    {
        #if NETCODE_DEBUG
        private bool m_DidPrintError;
        #endif
        private EntityQuery m_Query;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            // Host World 仍需执行此校验，以允许存在不与权威 World 交互的 Physics Scene，例如 Host World 仍然需要布娃娃
            // 非调试模式下，要求存在该 Singleton 才更新
            #if !NETCODE_DEBUG
            state.RequireForUpdate<PredictedPhysicsNonGhostWorld>();
            #endif
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<PhysicsVelocity, PhysicsWorldIndex>()
                .WithNone<GhostInstance>();
            m_Query = state.GetEntityQuery(builder);
            m_Query.SetSharedComponentFilter(new PhysicsWorldIndex(0));
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!m_Query.IsEmpty)
            {
                if (SystemAPI.TryGetSingleton<PredictedPhysicsNonGhostWorld>(out var targetWorld))
                {
                    // 遍历所有对象并设置新的目标 World，此操作属于结构性变更，需要谨慎处理
                    state.EntityManager.SetSharedComponent(m_Query, new PhysicsWorldIndex(targetWorld.Value));
                }
                #if NETCODE_DEBUG
                else if (!m_DidPrintError)
                {
                    // 调试模式下只输出一次警告，说明处理方法并展示最先发现的问题 Entity，便于调试
                    var erredEntities = m_Query.ToEntityArray(Allocator.Temp);
                    FixedString512Bytes error = $"[{state.WorldUnmanaged.Name}] The default physics world contains {erredEntities.Length} dynamic physics objects which are not ghosts. This is not supported! In order to have client-only physics, you must setup a custom physics world:";
                    foreach (var erredEntity in erredEntities)
                    {
                        FixedString512Bytes tempFs = "\n- ";
                        tempFs.Append(erredEntity.ToFixedString());
                        tempFs.Append(' ');
                        state.EntityManager.GetName(erredEntity, out var entityName);
                        tempFs.Append(entityName);

                        var formatError = error.Append(tempFs);
                        if (formatError == FormatError.Overflow)
                            break;
                    }
                    SystemAPI.GetSingleton<NetDebug>().LogError(error);
                    m_DidPrintError = true;
                    state.RequireForUpdate<PredictedPhysicsNonGhostWorld>();
                }
                #endif
            }
        }
    }

    /// <summary>
    /// 确保预测模式切换平滑在 Physics 运动平滑之后执行并覆盖其结果的系统
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateBefore(typeof(SwitchPredictionSmoothingSystem))]
    [UpdateAfter(typeof(SmoothRigidBodiesGraphicalMotion))]
    public partial class SwitchPredictionSmoothingPhysicsOrderingSystem : SystemBase
    {
        internal struct Disabled : IComponentData
        {}
        protected override void OnCreate()
        {
            RequireForUpdate<Disabled>();
        }

        protected override void OnUpdate()
        {
        }
    }
}
