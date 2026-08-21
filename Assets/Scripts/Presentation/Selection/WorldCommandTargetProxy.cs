using UnityEngine;
using Unity.Entities;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 记录场景碰撞体对应的 ECS Entity，供点击射线解析目标
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.Selection", "AnimarsCatcher.Presentation", "MovementSelectableProxy")]
    public class WorldCommandTargetProxy : MonoBehaviour
    {
        public Entity Entity { get; private set; }

        /// <summary>
        /// 绑定当前视图对应的 ECS Entity
        /// </summary>
        /// <param name="entity">当前视图对应的 Entity</param>
        public void Bind(Entity entity)
        {
            Entity = entity;
        }
    }
}
