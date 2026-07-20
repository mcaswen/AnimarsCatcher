using Unity.Entities;
using UnityEngine;
using UnityEngine.Serialization;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 配置服务端全局比赛资源状态的初始值
    /// </summary>
    public class GlobalGameResourceStateAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("initialTimeSeconds")]
        [SerializeField] private int _initialTimeSeconds;

        private sealed class Baker : Baker<GlobalGameResourceStateAuthoring>
        {
            public override void Bake(GlobalGameResourceStateAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);

                AddComponent(entity, new GlobalGameResourceState
                {
                    MatchTimeSeconds = authoring._initialTimeSeconds
                });

                AddComponent<GlobalGameResourceTag>(entity);
            }
        }
    }
}
