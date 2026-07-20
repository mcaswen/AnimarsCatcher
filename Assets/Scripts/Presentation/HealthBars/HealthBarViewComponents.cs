using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Presentation.HealthBars
{
    /// <summary>
    /// ECS 实体持有的托管血条预制体配置
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.HealthUI", "AnimarsCatcher.Presentation", "HealthBarViewPrefab")]
    public class HealthBarViewConfig : IComponentData
    {
        public GameObject HealthBarPrefab;
        public float3 WorldOffset;
    }

    /// <summary>
    /// 标识目标实体已经创建客户端血条视图
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.HealthUI", "AnimarsCatcher.Presentation", "HealthBarViewSpawnedTag")]
    public struct HealthBarViewSpawnedTag : IComponentData { }
}
