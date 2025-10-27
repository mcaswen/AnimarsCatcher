using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

public static class BlasterAniIDs
{
    // StateId（注意：0 预留给通用 S0，这里从 1 开始）
    public readonly static ushort S_Follow = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, 1);
    public readonly static ushort S_Find  = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, 2);
    public readonly static ushort S_Shoot = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, 3);
    public readonly static ushort S_Idle = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, 4);

    // ConditionId
    public const ushort C_OnAssignedToTarget = 1; 
    public const ushort C_EnteredShootRange = 2; 
    public const ushort C_LostTarget = 3; 
    public const ushort C_OnAssignedToPlayer = 4;

    // ActionId
    public const ushort A_EnterFollow   = 1;
    public const ushort A_ExitFollow    = 2;
    public const ushort A_EnterFind    = 3;
    public const ushort A_ExitFind     = 4;
    public const ushort A_EnterShoot   = 5;
    public const ushort A_ExitShoot = 6;
    public const ushort A_EnterIdle = 7;
    public const ushort A_ExitIdle = 8;
    public const ushort A_TickCooldown  = 7;   // 轻量每帧动作：冷却递减
}

public static class BlasterAniBlackBoardKeys 
{
    public const uint K_ShootAnimationParam = 0x01u;  // int
    public const uint K_Distance        = 0x02u;  // float
    public const uint K_AttackRange     = 0x03u;  // float
    public const uint K_ChaseStartDist  = 0x04u;  // float
    public const uint K_Cooldown        = 0x05u;  // float
    public const uint K_CooldownReset   = 0x06u;  // float
}