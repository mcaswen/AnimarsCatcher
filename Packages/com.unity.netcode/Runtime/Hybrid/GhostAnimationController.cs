using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Animations;

using Unity.Entities;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Transforms;

namespace Unity.NetCode.Hybrid
{
    /// <summary>
    /// GhostAnimationController 用于判断哪些对象需要托管更新调用，
    /// 哪些对象可以使用基于 System 的快速路径的组件
    /// </summary>
    public struct EnableAnimationControllerPredictionUpdate : IComponentData
    {}

    /// <summary>
    /// Ghost Animation Controller 是支持通过 NetCode for Entities 进行 Ghost 同步的特殊 Animation Graph
    /// 必须添加到由 Entity 通过 GhostPresentationGameObjectPrefabReference 引用的 GameObject
    /// Controller 具有单个 Graph Asset，但该 Asset 可以递归并包含完整 Graph
    /// </summary>
    [RequireComponent(typeof(Animator), typeof(GhostPresentationGameObjectEntityOwner))]
    [DisallowMultipleComponent]
    [HelpURL(HelpURLs.GhostAnimationController)]
    public class GhostAnimationController : MonoBehaviour, IRegisterPlayableData
    {
        interface IAnimationDataReference : IDisposable
        {
            void CopyFromEntity(EntityManager entityManager, Entity entity);
            void CopyToEntity(EntityManager entityManager, Entity entity);
        }
        class AnimationDataReference<T> : IAnimationDataReference where T: unmanaged, IComponentData
        {
            public NativeReference<T> Value;
            public void CopyFromEntity(EntityManager entityManager, Entity entity)
            {
                Value.Value = entityManager.GetComponentData<T>(entity);
            }
            public void CopyToEntity(EntityManager entityManager, Entity entity)
            {
                entityManager.SetComponentData(entity, Value.Value);
            }
            public void Dispose()
            {
                Value.Dispose();
            }
        }

        /// <summary>
        /// 此 Controller 使用的 Graph Asset
        /// </summary>
        public GhostAnimationGraphAsset AnimationGraphAsset;
        /// <summary>
        /// 设为 true 时，Animation Graph 会作为预测更新的一部分进行求值，使 Skeleton 立即更新
        /// 设为 false 时，Pose 只会在全部系统运行后每帧更新一次，因此会产生一帧延迟，
        /// 同时 Root Motion 也无法工作
        /// </summary>
        public bool EvaluateGraphInPrediction;
        /// <summary>
        /// 设为 true 时，即使 Animation Node 中指定了事件，Animation System 也不会触发
        /// 主要用于复用包含事件的 Asset，但 Entity 版本不处理这些事件的情况
        /// </summary>
        public bool IgnoreEvents;
        private bool m_ApplyRootMotion;
        /// <summary>
        /// 此 Controller 使用 Root Motion 时返回 true
        /// 仅当 Animator 支持 Root Motion、Graph 在预测中求值且 Ghost 为 Predicted Ghost 时才为 true
        /// 使用 Owner Prediction 时，本地玩家角色属于 Predicted Ghost
        /// Graph Asset 可以访问此属性，以便在启用 Root Motion 时调整行为
        /// </summary>
        public bool ApplyRootMotion => m_ApplyRootMotion;

        private GhostPresentationGameObjectEntityOwner m_EntityOwner;
        private PlayableGraph m_PlayableGraph;
        internal List<GhostPlayableBehaviour> m_PlayableBehaviours;

        Dictionary<Type, IAnimationDataReference> m_References = new Dictionary<Type, IAnimationDataReference>();
        #if ENABLE_UNITY_COLLECTIONS_CHECKS
        internal bool m_IsPredictionUpdate;
        #endif

        private Transform m_Transform;

        /// <summary>
        /// IRegisterPlayableData 的实现，不应直接调用
        /// </summary>
        /// <typeparam name="T"><see cref="IComponentData"/> 的 Unmanaged 类型</typeparam>
        public void RegisterPlayableData<T>() where T: unmanaged, IComponentData
        {
            if (!m_EntityOwner.World.EntityManager.HasComponent<T>(m_EntityOwner.Entity))
                throw new InvalidOperationException("Playable data registration failed");
            if (!m_References.ContainsKey(typeof(T)))
            {
                // 为数据副本分配内存
                var reference = new AnimationDataReference<T>();
                reference.Value = new NativeReference<T>(Allocator.Persistent);
                m_References[typeof(T)] = reference;
            }
        }
        /// <summary>
        /// 获取 Graph Asset 注册的 Playable Data 副本
        /// 可以随时调用
        /// </summary>
        /// <typeparam name="T"><see cref="IComponentData"/> 的 Unmanaged 类型</typeparam>
        /// <returns><typeparamref name="T"/> 类型的 Playable Data 副本</returns>
        public unsafe T GetPlayableData<T>() where T: unmanaged, IComponentData
        {
            if (!m_References.ContainsKey(typeof(T)))
                throw new InvalidOperationException($"Trying to get playable data of type {typeof(T)}, but it has not been registered");
            AnimationDataReference<T> reference = m_References[typeof(T)] as AnimationDataReference<T>;
            return reference.Value.Value;
        }
        /// <summary>
        /// 获取 Graph Asset 注册的 Playable Data 引用
        /// 只能从 PreparePredictedData 调用
        /// </summary>
        /// <typeparam name="T"><see cref="IComponentData"/> 的 Unmanaged 类型</typeparam>
        /// <returns><typeparamref name="T"/> 类型的 Playable Data 引用</returns>
        public unsafe ref T GetPlayableDataRef<T>() where T: unmanaged, IComponentData
        {
            #if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!m_IsPredictionUpdate)
                throw new InvalidOperationException("GetPlayableDataRef can only be called from PreparePredictedData, use GetPlayableData without ref to read the data outside the prediction update");
            #endif
            AnimationDataReference<T> reference = m_References[typeof(T)] as AnimationDataReference<T>;
            // 查找指针，转换为引用后返回
            return ref UnsafeUtility.AsRef<T>(reference.Value.GetUnsafePtr());
        }
        /// <summary>
        /// 获取与 Controller 关联 Entity 上组件的数据副本
        /// 只能从 PreparePredictedData 调用
        /// </summary>
        /// <typeparam name="T"><see cref="IComponentData"/> 的 Unmanaged 类型</typeparam>
        /// <returns><typeparamref name="T"/> 类型的组件数据副本</returns>
        public T GetEntityComponentData<T>() where T: unmanaged, IComponentData
        {
            #if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!m_IsPredictionUpdate)
                throw new InvalidOperationException("Reading entity data is only allowed from PreparePredictedData, use RegisterPlayableData/GetPlayableData to access data outside the prediction loop");
            #endif
            return m_EntityOwner.World.EntityManager.GetComponentData<T>(m_EntityOwner.Entity);
        }
        /// <summary>
        /// 修改与 Controller 关联 Entity 上的组件数据
        /// 只能从 PreparePredictedData 调用
        /// </summary>
        /// <param name="data">要赋给 Entity 的数据</param>
        /// <typeparam name="T"><see cref="IComponentData"/> 的 Unmanaged 类型</typeparam>
        public void SetEntityComponentData<T>(T data) where T: unmanaged, IComponentData
        {
            #if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!m_IsPredictionUpdate)
                throw new InvalidOperationException("Writing entity data is only allowed from PreparePredictedData");
            #endif
            m_EntityOwner.World.EntityManager.SetComponentData<T>(m_EntityOwner.Entity, data);
        }
        /// <summary>
        /// 获取与 Controller 关联 Entity 上组件的 DynamicBuffer
        /// 只能从 PreparePredictedData 调用
        /// </summary>
        /// <typeparam name="T"><see cref="IBufferElementData"/> 的 Unmanaged 类型</typeparam>
        /// <returns>Controller 上 <typeparamref name="T"/> 类型组件的 <see cref="DynamicBuffer{T}"/></returns>
        public DynamicBuffer<T> GetEntityBuffer<T>() where T: unmanaged, IBufferElementData
        {
            #if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!m_IsPredictionUpdate)
                throw new InvalidOperationException("Reading entity data is only allowed from PreparePredictedData, use RegisterPlayableData/GetPlayableData to access data outside the prediction loop");
            #endif
            return m_EntityOwner.World.EntityManager.GetBuffer<T>(m_EntityOwner.Entity);
        }
        internal void CopyFromEntities()
        {
            foreach (var entry in m_References.Values)
                entry.CopyFromEntity(m_EntityOwner.World.EntityManager, m_EntityOwner.Entity);
        }
        internal void CopyToEntities()
        {
            foreach (var entry in m_References.Values)
                entry.CopyToEntity(m_EntityOwner.World.EntityManager, m_EntityOwner.Entity);
        }

        internal void EvaluateGraph(float deltaTime)
        {
            if (m_PlayableBehaviours == null)
                return;
            if (m_ApplyRootMotion)
            {
                m_Transform.localPosition = m_EntityOwner.World.EntityManager.GetComponentData<LocalTransform>(m_EntityOwner.Entity).Position;
                m_Transform.localRotation = m_EntityOwner.World.EntityManager.GetComponentData<LocalTransform>(m_EntityOwner.Entity).Rotation;
            }
            m_PlayableGraph.Evaluate(deltaTime);
            if (m_ApplyRootMotion)
            {
                m_EntityOwner.World.EntityManager.SetComponentData(m_EntityOwner.Entity, LocalTransform.FromPositionRotation(m_Transform.localPosition, m_Transform.localRotation));
            }
        }

        void Start()
        {
            m_Transform = gameObject.transform;
            var animator = GetComponent<Animator>();
            m_EntityOwner = GetComponent<GhostPresentationGameObjectEntityOwner>();
            var isPredicted = m_EntityOwner.World.EntityManager.HasComponent<PredictedGhost>(m_EntityOwner.Entity);
            // 从 Asset 创建 Playable Graph
            m_PlayableGraph = PlayableGraph.Create(gameObject.name);

            if (IgnoreEvents)
                animator.fireEvents = false;

            // 手动更新 Graph
            if (EvaluateGraphInPrediction && isPredicted)
                m_PlayableGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            else
            {
                // 对插值 Ghost 或未启用预测更新的情况禁用 Root Motion
                animator.applyRootMotion = false;
                m_PlayableGraph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            }
            m_ApplyRootMotion = animator.applyRootMotion;

            m_PlayableBehaviours = new List<GhostPlayableBehaviour>();
            AnimationGraphAsset.RegisterPlayableData(this);
            var playable = AnimationGraphAsset.CreatePlayable(this, m_PlayableGraph, m_PlayableBehaviours);

            var playableOutput = AnimationPlayableOutput.Create(m_PlayableGraph, "Animator", animator);
            playableOutput.SetSourcePlayable(playable, 0);

            m_PlayableGraph.Play();

            if (m_PlayableBehaviours.Count > 0 || EvaluateGraphInPrediction)
                m_EntityOwner.World.EntityManager.AddComponentData(m_EntityOwner.Entity, default(EnableAnimationControllerPredictionUpdate));
        }

        void OnDestroy()
        {
            // 销毁 Playable Graph
            m_PlayableGraph.Destroy();
            foreach (var entry in m_References.Values)
                entry.Dispose();
            m_References.Clear();
        }
    }

    /// <summary>
    /// 为所有已注册的 Ghost Animation Controller 调用 PreparePredictedData，
    /// 并在启用时触发 Graph 求值的系统
    /// </summary>
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    public partial class GhostAnimationControllerPredictionSystem : SystemBase
    {
        GhostPresentationGameObjectSystem m_GhostPresentationGameObjectSystem;
        protected override void OnCreate()
        {
            m_GhostPresentationGameObjectSystem = World.GetExistingSystemManaged<GhostPresentationGameObjectSystem>();
            RequireForUpdate(GetEntityQuery(ComponentType.ReadOnly<GhostPresentationGameObjectPrefabReference>(), ComponentType.ReadOnly<EnableAnimationControllerPredictionUpdate>()));
        }
        protected override void OnUpdate()
        {
            var predictionTick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
            var prevTick = predictionTick;
            prevTick.Decrement();
            var deltaTime = SystemAPI.Time.DeltaTime;
            var entitiesWithPredictedGhostsQuery = SystemAPI.QueryBuilder()
                .WithAll<GhostPresentationGameObjectPrefabReference>()
                .WithAll<Simulate>()
                .WithAll<PredictedGhost>()
                .WithAll<EnableAnimationControllerPredictionUpdate>().Build();
            var entitiesWithPredictedGhosts = entitiesWithPredictedGhostsQuery.ToEntityArray(Allocator.Temp);
            var predictedGhostData = entitiesWithPredictedGhostsQuery.ToComponentDataArray<PredictedGhost>(Allocator.Temp);
            for (int i = 0; i < entitiesWithPredictedGhosts.Length; i++)
            {
                var isRollback = !predictedGhostData[i].ShouldPredict(prevTick);
                var entity = entitiesWithPredictedGhosts[i];
                var go = m_GhostPresentationGameObjectSystem.GetGameObjectForEntity(EntityManager, entity);
                var ctrl = go?.GetComponent<GhostAnimationController>();
                if (ctrl == null)
                    return;
                ctrl.CopyFromEntities();
                if (ctrl.m_PlayableBehaviours.Count > 0)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    ctrl.m_IsPredictionUpdate = true;
#endif
                    foreach (var behaviour in ctrl.m_PlayableBehaviours)
                        behaviour.PreparePredictedData(predictionTick, deltaTime, isRollback);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    ctrl.m_IsPredictionUpdate = false;
#endif
                    ctrl.CopyToEntities();
                }
                if (ctrl.EvaluateGraphInPrediction)
                {
                    ctrl.EvaluateGraph(deltaTime);
                }
            }
        }
    }
    /// <summary>
    /// 确保已注册的 Playable Data 在插值 Ghost 执行 PrepareFrame 前完成更新，
    /// 同时处理未在预测中使用 PreparePredictedData 或 Graph 更新的 Predicted Ghost
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class GhostAnimationControllerInterpolationSystem : SystemBase
    {
        GhostPresentationGameObjectSystem m_GhostPresentationGameObjectSystem;
        protected override void OnCreate()
        {
            m_GhostPresentationGameObjectSystem = World.GetExistingSystemManaged<GhostPresentationGameObjectSystem>();
            RequireForUpdate<GhostPresentationGameObjectPrefabReference>();
        }

        protected override void OnUpdate()
        {
            var entitiesWithoutPredictedGhostsQuery = SystemAPI.QueryBuilder()
                .WithAll<GhostPresentationGameObjectPrefabReference>().WithNone<PredictedGhost>().Build();
            var entitiesWithoutPredictedGhosts = entitiesWithoutPredictedGhostsQuery.ToEntityArray(Allocator.Temp);
            foreach (var entity in entitiesWithoutPredictedGhosts)
            {
                var go = m_GhostPresentationGameObjectSystem.GetGameObjectForEntity(EntityManager, entity);
                var ctrl = go?.GetComponent<GhostAnimationController>();
                if (ctrl != null)
                    ctrl.CopyFromEntities();
            }

            var entitiesWithPredictedGhostsQuery = SystemAPI.QueryBuilder()
                .WithAll<GhostPresentationGameObjectPrefabReference>()
                .WithAll<PredictedGhost>()
                .WithNone<EnableAnimationControllerPredictionUpdate>().Build();
            var entitiesWithPredictedGhosts = entitiesWithPredictedGhostsQuery.ToEntityArray(Allocator.Temp);
            foreach (var entity in entitiesWithPredictedGhosts)
            {
                var go = m_GhostPresentationGameObjectSystem.GetGameObjectForEntity(EntityManager, entity);
                var ctrl = go?.GetComponent<GhostAnimationController>();
                if (ctrl != null && ctrl.m_PlayableBehaviours != null && ctrl.m_PlayableBehaviours.Count == 0)
                    ctrl.CopyFromEntities();
            }
        }
    }
    /// <summary>
    /// 确保服务器上未在预测中使用 PreparePredictedData 或 Graph 更新的 Predicted Ghost，
    /// 在执行 PrepareFrame 前完成已注册 Playable Data 更新的系统
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast=true)]
    [UpdateAfter(typeof(EndSimulationEntityCommandBufferSystem))]
    public partial class GhostAnimationControllerServerSystem : SystemBase
    {
        GhostPresentationGameObjectSystem m_GhostPresentationGameObjectSystem;
        protected override void OnCreate()
        {
            m_GhostPresentationGameObjectSystem = World.GetExistingSystemManaged<GhostPresentationGameObjectSystem>();
            RequireForUpdate<GhostPresentationGameObjectPrefabReference>();
        }
        protected override void OnUpdate()
        {
            var query = SystemAPI.QueryBuilder().WithAll<GhostPresentationGameObjectPrefabReference>()
                            .WithAll<PredictedGhost>()
                            .WithNone<EnableAnimationControllerPredictionUpdate>().Build();

            var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                var go = m_GhostPresentationGameObjectSystem.GetGameObjectForEntity(EntityManager, entity);
                var ctrl = go?.GetComponent<GhostAnimationController>();
                if (ctrl != null && ctrl.m_PlayableBehaviours != null && ctrl.m_PlayableBehaviours.Count == 0)
                    ctrl.CopyFromEntities();
            }
        }
    }
}
