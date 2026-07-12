using Unity.Entities;
using UnityEngine;

/// <summary>
/// 配置服务端全局比赛资源状态的初始值
/// </summary>
public class GlobalGameResourceStateAuthoring : MonoBehaviour
{
    public int initialTimeSeconds;

    /// <summary>
    /// 创建全局资源状态单例实体
    /// </summary>
    class Baker : Baker<GlobalGameResourceStateAuthoring>
    {
        /// <summary>
        /// 烘焙比赛计时状态和单例标签
        /// </summary>
        public override void Bake(GlobalGameResourceStateAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);

            AddComponent(entity, new GlobalGameResourceState
            {
                MatchTimeSeconds = authoring.initialTimeSeconds
            });

            AddComponent<GlobalGameResourceTag>(entity);
        }
    }
}
