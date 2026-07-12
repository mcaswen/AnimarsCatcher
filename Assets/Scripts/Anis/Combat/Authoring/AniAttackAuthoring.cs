using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 为 Ani 声明攻击冷却、目标和开火请求等运行时状态
/// </summary>
public class AniAttackAuthoring : MonoBehaviour
{
    /// <summary>
    /// 将攻击状态初始化到 Ani 实体
    /// </summary>
    public class Baker : Baker<AniAttackAuthoring>
    {
        /// <summary>
        /// 烘焙无目标、可立即攻击的初始状态
        /// </summary>
        /// <param name="authoring">Ani 攻击创作组件</param>
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
