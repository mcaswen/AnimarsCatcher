using UnityEngine;
using Unity.Entities;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 将场景碰撞体命中结果桥接回对应的 ECS 实体
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.Selection", "AnimarsCatcher.Presentation", "MovementSelectableProxy")]
    public class WorldCommandTargetProxy : MonoBehaviour
    {
        public Entity Entity { get; private set; }

        /// <summary>
        /// 绑定当前视图对应的 ECS 实体
        /// </summary>
        /// <param name="entity">当前视图对应的实体</param>
        public void Bind(Entity entity)
        {
            Entity = entity;
        }
    }
}
