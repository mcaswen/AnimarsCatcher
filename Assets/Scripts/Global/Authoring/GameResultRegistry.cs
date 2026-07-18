using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using UnityEngine;

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
        /// <summary>
        /// 创建供服务器胜负系统查询的对局结果组件
        /// </summary>
        /// <param name="authoring">对局结果注册组件</param>
        public override void Bake(GameResultRegistry authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent<GameResult>(entity);
        }
    }
}
