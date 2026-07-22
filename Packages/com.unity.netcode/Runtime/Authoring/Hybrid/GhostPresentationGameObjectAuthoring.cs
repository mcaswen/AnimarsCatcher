using Unity.Entities;
using UnityEngine;
using System;
using System.Collections.Generic;

namespace Unity.NetCode.Hybrid
{
    /// <summary>
    /// 将此组件添加到 Ghost Prefab，以配置该 Ghost 的表现层 GameObject
    /// </summary>
    /// <remarks>
    /// 如果 <see cref="ServerPrefab"/> 或 <see cref="ClientPrefab"/> 不为 null，烘焙过程会创建一个新的附加 Entity，
    /// 并为其添加包含 Prefab 引用的 <see cref="GhostPresentationGameObjectPrefab"/> 托管组件
    /// 同时还会向转换后的 Entity 添加 <see cref="GhostPresentationGameObjectPrefabReference"/>，使其引用新创建的 Entity
    /// 最后将自身注册为 IRegisterPlayableData 的生产者
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL(Authoring.HelpURLs.GhostPresentationGameObjectAuthoring)]
    public class GhostPresentationGameObjectAuthoring : MonoBehaviour
#if !UNITY_DISABLE_MANAGED_COMPONENTS
        , IRegisterPlayableData
#endif
    {
        /// <summary>
        /// 在服务器上用作 Entity 视觉表现的 GameObject Prefab
        /// 更多信息参见 <see cref="GhostPresentationGameObjectPrefab"/>
        /// </summary>
        public GameObject ServerPrefab;
        /// <summary>
        /// 在客户端上用作 Entity 视觉表现的 GameObject Prefab
        /// 更多信息参见 <see cref="GhostPresentationGameObjectPrefab"/>
        /// </summary>
        public GameObject ClientPrefab;
        private EntityManager regEntityManager;
        private Entity regEntity;

#if !UNITY_DISABLE_MANAGED_COMPONENTS
        /// <summary>
        /// <see cref="IRegisterPlayableData"/> 的实现，不应直接调用
        /// 此方法会作为 GhostAnimationController 初始化流程的一部分被调用
        /// </summary>
        /// <typeparam name="T">PlayableComponent 类型</typeparam>
        public void RegisterPlayableData<T>() where T: unmanaged, IComponentData
        {
            regEntityManager.AddComponentData(regEntity, default(T));
        }
#endif
    }

    [BakingVersion("cmarastoni", 1)]
    class GhostPresentationGameObjectBaker : Baker<GhostPresentationGameObjectAuthoring>
#if !UNITY_DISABLE_MANAGED_COMPONENTS
        , IRegisterPlayableData
#endif
    {
        private HashSet<Type> m_AddedTypes;
        public override void Bake(GhostPresentationGameObjectAuthoring authoring)
        {
#if UNITY_DISABLE_MANAGED_COMPONENTS
            throw new System.InvalidOperationException("GhostPresentationGameObjects require managed components to be enabled");
#else
            bool isPrefab = !authoring.gameObject.scene.IsValid() || (GetComponent<GhostAuthoringComponent>()?.ForcePrefabConversion ?? false);

            var target = this.GetNetcodeTarget(isPrefab);

            var prefabComponent = new GhostPresentationGameObjectPrefab
            {
                Client = (target == NetcodeConversionTarget.Server) ? null : authoring.ClientPrefab,
                Server = (target == NetcodeConversionTarget.Client) ? null : authoring.ServerPrefab
            };
            if (prefabComponent.Server == null && prefabComponent.Client == null)
                return;
            var presPrefab = CreateAdditionalEntity(TransformUsageFlags.None);
            AddComponentObject(presPrefab, prefabComponent);

            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new GhostPresentationGameObjectPrefabReference{Prefab = presPrefab});

            // 注册动画数据所需的全部组件
            m_AddedTypes = new HashSet<Type>();
            if (prefabComponent.Client != null)
            {
                var anim = GetComponent<GhostAnimationController>(prefabComponent.Client);
                if (anim != null && anim.AnimationGraphAsset != null)
                    anim.AnimationGraphAsset.RegisterPlayableData(this);
            }
            if (prefabComponent.Server != null)
            {
                var anim = GetComponent<GhostAnimationController>(prefabComponent.Server);
                if (anim != null && anim.AnimationGraphAsset != null)
                    anim.AnimationGraphAsset.RegisterPlayableData(this);
            }
#endif
        }

#if !UNITY_DISABLE_MANAGED_COMPONENTS
        public void RegisterPlayableData<T>() where T: unmanaged, IComponentData
        {
            if (m_AddedTypes.Contains(typeof(T)))
                return;
            m_AddedTypes.Add(typeof(T));
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, default(T));
        }
#endif
    }
}
