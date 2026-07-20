using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 为 Ani 声明攻击冷却、目标和开火请求等运行时状态
    /// </summary>
    public class AniAttackAuthoring : MonoBehaviour
    {
        private sealed class Baker : Baker<AniAttackAuthoring>
        {
            public override void Bake(AniAttackAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponent(entity, new AniAttackState
                {
                    CooldownRemaining = 0f
                });
                AddComponent(entity, new AniAttackTarget
                {
                    Target = Entity.Null,
                    Kind   = AniAttackTargetKind.None
                });

                AddComponent<AniAttackFireRequest>(entity);
            }
        }
    }
}
