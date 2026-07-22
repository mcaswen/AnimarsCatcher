using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// 向 Entity 添加 maxDist 组件的 Authoring 组件
    /// </summary>
    [DisallowMultipleComponent]
    [HelpURL(Authoring.HelpURLs.DefaultSmoothingActionUserParamsAuthoring)]
    public class DefaultSmoothingActionUserParamsAuthoring : MonoBehaviour
    {
        [RegisterBinding(typeof(DefaultSmoothingActionUserParams), "maxDist")]
        [SerializeField] private float maxDist;
        [RegisterBinding(typeof(DefaultSmoothingActionUserParams), "delta")]
        [SerializeField] private float delta;

        [BakingVersion("cmarastoni", 1)]
        class DefaultUserParamsBaker : Baker<DefaultSmoothingActionUserParamsAuthoring>
        {
            public override void Bake(DefaultSmoothingActionUserParamsAuthoring authoring)
            {
                var component = default(DefaultSmoothingActionUserParams);
                component.maxDist = authoring.maxDist;
                component.delta = authoring.delta;
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, component);
            }
        }
    }
}
