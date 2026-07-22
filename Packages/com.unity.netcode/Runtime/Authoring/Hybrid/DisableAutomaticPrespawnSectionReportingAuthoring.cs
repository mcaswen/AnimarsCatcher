using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// 向 Entity 添加 DisableAutomaticPrespawnSectionReporting 组件的 Authoring 组件
    /// </summary>
    [UnityEngine.DisallowMultipleComponent]
    [HelpURL(Authoring.HelpURLs.DisableAutomaticPrespawnSectionReportingAuthoring)]
    public class DisableAutomaticPrespawnSectionReportingAuthoring : UnityEngine.MonoBehaviour
    {
        [BakingVersion("cmarastoni", 1)]
        class DisableAutomaticPrespawnSectionReportingBaker : Baker<DisableAutomaticPrespawnSectionReportingAuthoring>
        {
            public override void Bake(DisableAutomaticPrespawnSectionReportingAuthoring authoring)
            {
                DisableAutomaticPrespawnSectionReporting component = default(DisableAutomaticPrespawnSectionReporting);
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, component);
            }
        }
    }
}
