using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public class AniAttackAuthoring : MonoBehaviour
{
    public class Baker : Baker<AniAttackAuthoring>
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