using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 保存 Ani 的服务器权威移动、攻击和归属参数
    /// </summary>
    [GhostComponent]
    public struct AniAttributes : IComponentData
    {
        [GhostField]
        public float MovementSpeed;

        public float AttackInterval;

        public int AttackDamage;

        public float AttackRange;
        public AniAttackMode AttackMode;

        [GhostField]
        public int OwnerPlayerId;
    }

    /// <summary>
    /// 指定 Ani 使用近战或远程攻击结算链路
    /// </summary>
    public enum AniAttackMode : byte
    {
        Melee  = 0,
        Ranged = 1,
    }
}
