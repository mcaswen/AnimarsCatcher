using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using UnityEngine;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在场景中声明唯一的对局结果实体
    /// </summary>
    public class GameResultRegistry : MonoBehaviour
    {
        /// <summary>
        /// 将对局结果注册点转换为实体组件
        /// </summary>
        public class Baker : Baker<GameResultRegistry>
        {
            public override void Bake(GameResultRegistry authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent<GameResult>(entity);
            }
        }
    }
}
