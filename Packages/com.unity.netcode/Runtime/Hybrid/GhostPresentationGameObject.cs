using Unity.Entities;
using UnityEngine;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Transforms;
using Unity.Collections;
using UnityEngine.Jobs;

namespace Unity.NetCode.Hybrid
{
    /// <summary>
    /// 用作 Entity 视觉表现的 GameObject Prefab
    /// </summary>
    public class GhostPresentationGameObjectPrefab : IComponentData
    {
        /// <summary>
        /// 在服务器上用作 Entity 视觉表现的 GameObject Prefab
        /// 服务器实例通常不可见，但在服务器上运行动画等场景中仍然需要它
        /// </summary>
        public GameObject Server;
        /// <summary>
        /// 在客户端上用作 Entity 视觉表现的 GameObject Prefab
        /// 插值 Ghost 和 Predicted Ghost 不能使用不同的 GameObject，否则会破坏预测模式切换
        /// </summary>
        public GameObject Client;
    }
    /// <summary>
    /// 对包含 GhostPresentationGameObjectPrefab 的 Entity 的引用
    /// 为避免所有 Ghost 都带有托管组件，GameObject Prefab 不会直接存储在 Ghost 上
    /// 系统会创建单独的 Entity 存储该托管组件，而此组件保存对该 Entity 的引用
    /// </summary>
    public struct GhostPresentationGameObjectPrefabReference : IComponentData
    {
        /// <summary>
        /// 包含待实例化 GameObject Prefab 的 Entity
        /// </summary>
        public Entity Prefab;
    }
    /// <summary>
    /// 用于跟踪哪些 GameObject Prefab 已初始化的内部状态
    /// </summary>
    internal struct GhostPresentationGameObjectState : ICleanupComponentData
    {
        /// <summary>
        /// <see cref="GhostPresentationGameObjectSystem"/> 用于获取此 Entity 对应 GameObject 实例的索引
        /// </summary>
        public int GameObjectIndex;
    }

    /// <summary>
    /// 为请求了表现对象的 Ghost 生成 GameObject 的系统
    /// 该系统紧随客户端生成代码运行，以确保 GameObject 立即创建
    /// 在服务器上，必须处理尚无表现 GameObject 的情况，例如通过 Cleanup Component 过滤，
    /// 或者在 BeginSimulationCommandBufferSystem 中完成全部生成操作，确保 GameObject 同时创建
    /// </summary>
    // 在客户端紧随 GhostSpawnSystemGroup，在服务器紧随 BeginSimulationEntityCommandBufferSystem
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup), OrderFirst = true)]
    public partial class GhostPresentationGameObjectSystem : SystemBase
    {
        internal List<GameObject> m_GameObjects;
        internal TransformAccessArray m_Transforms;
        internal NativeList<Entity> m_Entities;
        private EntityQuery m_NewPresentationQuery;
        private EntityQuery m_DisposedQuery;
        private ComponentLookup<GhostPresentationGameObjectState> m_GhostPresentationGameObjectStateLookup;

        /// <summary>
        /// 查找指定 Entity 的表现 GameObject
        /// Entity 不直接引用 GameObject，必须通过此方法查找
        /// </summary>
        /// <param name="entityManager">用于查找 <paramref name="ent"/> 的 EntityManager</param>
        /// <param name="ent">需要查找表现 <see cref="GameObject"/> 的 Entity</param>
        /// <returns>该 Entity 对应的 <see cref="GameObject"/></returns>
        public GameObject GetGameObjectForEntity(EntityManager entityManager, Entity ent)
        {
            if (!entityManager.HasComponent<GhostPresentationGameObjectState>(ent))
                return null;
            var idx = entityManager.GetComponentData<GhostPresentationGameObjectState>(ent).GameObjectIndex;
            if (idx < 0)
                return null;
            return m_GameObjects[idx];
        }
        protected override void OnCreate()
        {
            m_GameObjects = new List<GameObject>();
            // 容量和期望 Job 数量尚未经过充分优化
            m_Transforms = new TransformAccessArray(16, 16);
            m_Entities = new NativeList<Entity>(16, Allocator.Persistent);
            m_NewPresentationQuery = SystemAPI.QueryBuilder()
                .WithAll<GhostPresentationGameObjectPrefabReference>()
                .WithNone<GhostPresentationGameObjectState>()
                .Build();
            m_DisposedQuery = SystemAPI.QueryBuilder()
                .WithNone<GhostPresentationGameObjectPrefabReference>()
                .WithAll<GhostPresentationGameObjectState>()
                .Build();
            m_GhostPresentationGameObjectStateLookup = GetComponentLookup<GhostPresentationGameObjectState>();
        }
        protected override void OnDestroy()
        {
            foreach (var go in m_GameObjects)
            {
                if(!Application.isPlaying)
                    Object.DestroyImmediate(go);
                else
                    Object.Destroy(go);

            }
            m_GameObjects.Clear();
            m_Transforms.Dispose();
            m_Entities.Dispose();
        }
        protected override void OnUpdate()
        {
            var entitiesWithoutGhostPresentationGameObjectState = m_NewPresentationQuery.ToEntityArray(Allocator.Temp);
            var presentations = m_NewPresentationQuery.ToComponentDataArray<GhostPresentationGameObjectPrefabReference>(Allocator.Temp);
            EntityManager.AddComponent<GhostPresentationGameObjectState>(m_NewPresentationQuery);
            for (var i = 0; i < entitiesWithoutGhostPresentationGameObjectState.Length; ++i)
            {
                var entity = entitiesWithoutGhostPresentationGameObjectState[i];
                var presentation = presentations[i];

                var goPrefabEntity = EntityManager.GetComponentData<GhostPresentationGameObjectPrefab>(presentation.Prefab);
                var goPrefab = World.IsServer() ? goPrefabEntity.Server : goPrefabEntity.Client;
                int idx = -1;
                if (goPrefab != null)
                {
                    var go = GameObject.Instantiate(goPrefab);
                    var owner = go.GetComponent<GhostPresentationGameObjectEntityOwner>();
                    if (owner != null)
                    {
                        owner.Initialize(entity, World);
                    }
                    idx = m_GameObjects.Count;
                    m_GameObjects.Add(go);
                    m_Entities.Add(entity);
                    m_Transforms.Add(go.transform);
                }
                EntityManager.SetComponentData(entity, new GhostPresentationGameObjectState{GameObjectIndex = idx});
            }

            var disposedPresentationEntities = m_DisposedQuery.ToEntityArray(Allocator.Temp);
            m_GhostPresentationGameObjectStateLookup.Update(this);

            foreach (var disposedEntity in disposedPresentationEntities)
            {
                var state = m_GhostPresentationGameObjectStateLookup[disposedEntity];
                int idx = state.GameObjectIndex;
                if (idx >= 0)
                {
                    var lastIndex = m_GameObjects.Count - 1;
                    var lastEntity = m_Entities[lastIndex];
                    m_GhostPresentationGameObjectStateLookup[lastEntity] = new GhostPresentationGameObjectState { GameObjectIndex = idx };
                    if(!Application.isPlaying)
                        Object.DestroyImmediate(m_GameObjects[idx]);
                    else
                        Object.Destroy(m_GameObjects[idx]);
                    m_Transforms.RemoveAtSwapBack(idx);
                    m_Entities.RemoveAtSwapBack(idx);
                    m_GameObjects.RemoveAtSwapBack(idx);
                }
            }
            EntityManager.RemoveComponent<GhostPresentationGameObjectState>(m_DisposedQuery);
        }
    }

    /// <summary>
    /// 根据所属 Entity 的当前 Transform 更新表现 GameObject Transform 的系统
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateAfter(typeof(LocalToWorldSystem))]
    public partial class GhostPresentationGameObjectTransformSystem : SystemBase
    {
        private GhostPresentationGameObjectSystem m_GhostPresentationGameObjectSystem;
        protected override void OnCreate()
        {
            m_GhostPresentationGameObjectSystem = World.GetExistingSystemManaged<GhostPresentationGameObjectSystem>();
            RequireForUpdate(GetEntityQuery(ComponentType.ReadOnly<GhostPresentationGameObjectPrefabReference>()));
        }
        [BurstCompile]
        struct TransformUpdateJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeList<Entity> Entities;
            // 此处需要使用 LocalToWorld 的原因如下
            // Physics 和 NetCode for Entities 都可以通过直接修改 LTW，改变 Entity 在屏幕中的感知位置
            // 典型情况包括 Physics 插值或外推以及预测模式切换
            // 因此 Entity 与其渲染结果可能不同步，以下使用一维坐标简化说明
            //
            //  插值或预测模式切换
            //   |       (S)
            //   |     (D)
            //   | ------------------
            //
            //  外推
            //   |     (S)
            //   |       (D)
            //   | ------------------
            //
            //  模拟 Entity (S)
            //  显示 Entity (D)
            //
            // GameObject 是 Entity 在屏幕中的表现
            // 渲染位置可以选择 Entity 的本地模拟位置，或 LTW 表示的感知位置
            // 真正需要保证的是渲染位置与屏幕上的感知位置同步
            // 因此 GameObject 的位置必须取自 LTW，而 LTW 表示世界坐标
            // 不过 GameObject 通常是没有父级的根对象，因此可以设置 LocalPosition 而不是 Position
            //
            [ReadOnly] public ComponentLookup<LocalToWorld> TransformFromEntity;
            public void Execute(int index, TransformAccess transform)
            {
                var ent = Entities[index];
                transform.localPosition = TransformFromEntity[ent].Position;
                transform.localRotation = TransformFromEntity[ent].Rotation;
            }
        }
        protected override void OnUpdate()
        {
            var transformJob = new TransformUpdateJob
            {
                Entities = m_GhostPresentationGameObjectSystem.m_Entities,
                TransformFromEntity = GetComponentLookup<LocalToWorld>(true),
            };
            Dependency = transformJob.Schedule(m_GhostPresentationGameObjectSystem.m_Transforms, Dependency);
        }
    }
}
