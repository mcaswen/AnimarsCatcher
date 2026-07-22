using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Hybrid
{
    /// <summary>
    /// 将此组件添加到 GhostPresentationGameObjectPrefabReference 使用的 GameObject 后，
    /// 它会保存对此 GameObject 实例所属 Entity 和 World 的引用
    /// </summary>
    [DisallowMultipleComponent]
    [HelpURL(HelpURLs.GhostPresentationGameObjectEntityOwner)]
    public class GhostPresentationGameObjectEntityOwner : MonoBehaviour
    {
        /// <summary>
        /// 此 GameObject 所属 Entity 所在的 World
        /// </summary>
        public World World {get; internal set;}
        /// <summary>
        /// 此 GameObject 所属的 Entity
        /// </summary>
        public Entity Entity {get; internal set;}

        /// <summary>
        /// 初始化调试 Mesh 边界的便捷方法
        /// </summary>
        /// <param name="entity">此 GameObject 所属的 Entity</param>
        /// <param name="world">此 GameObject 所属 Entity 所在的 World</param>
        public void Initialize(Entity entity, World world)
        {
            Entity = entity;
            World = world;
#if UNITY_EDITOR
            var ghostBounds = new GhostDebugMeshBounds().Initialize(gameObject, entity, world);
            world.EntityManager.AddComponentData(entity, ghostBounds);
#endif
        }
    }
}
