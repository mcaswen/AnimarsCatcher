using Unity.Entities;
using UnityEngine;

/// <summary>
/// 配置服务端全局比赛资源状态的初始值
/// </summary>
public class GlobalGameResourceStateAuthoring : MonoBehaviour
{
    public int initialTimeSeconds;

    class Baker : Baker<GlobalGameResourceStateAuthoring>
    {
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
