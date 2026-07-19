using UnityEngine;
using Unity.Entities;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 将场景碰撞体命中结果桥接回对应的 ECS 实体
    /// </summary>
    public class MovementSelectableProxy : MonoBehaviour
    {
        public Entity Entity;
    }
}
