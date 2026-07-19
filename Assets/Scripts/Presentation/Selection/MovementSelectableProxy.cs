using UnityEngine;
using Unity.Entities;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 将场景碰撞体命中结果桥接回对应的 ECS 实体
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Gameplay", "AnimarsCatcher.Gameplay", "MovementSelectableProxy")]
    public class MovementSelectableProxy : MonoBehaviour
    {
        public Entity Entity;
    }
}
