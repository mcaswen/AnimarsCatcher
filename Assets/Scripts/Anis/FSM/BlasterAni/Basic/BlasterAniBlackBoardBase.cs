using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

public static class BlasterAniIDs
{
    // StateId， 0 预留给通用 S0，这里从 1 开始
    public readonly static ushort S_Idle = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, 1);
    public readonly static ushort S_Follow = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, 2);
    public readonly static ushort S_Find  = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, 3);
    public readonly static ushort S_Shoot = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, 4);


    // ConditionId
    public const ushort C_ShouldFollow = 1; // IsFollow == true
    public const ushort C_ShouldFind = 2; // Isshoot == true && TargetValid && !CanShoot
    public const ushort C_CanShoot = 3; // Isshoot == true && TargetValid && CanShoot
    public const ushort C_TargetGone = 4; // !TargetValid
    public const ushort C_StopShooting = 5; // Isshoot == false

    // ActionId
    public const ushort A_EnterFollow   = 1;
    public const ushort A_ExitFollow    = 2;
    public const ushort A_EnterFind    = 3;
    public const ushort A_ExitFind     = 4;
    public const ushort A_EnterShoot   = 5;
    public const ushort A_ExitShoot = 6;
    public const ushort A_EnterIdle = 7;
    public const ushort A_ExitIdle = 8;
}

public static class BlasterAniBlackBoardKeys
{
    // 输入与感知
    public const uint K_IsFollow = 0x001u;  // bool
    public const uint K_IsShoot = 0x002u;  // bool
    public const uint K_TargetValid = 0x003u;  // bool
    public const uint K_CanShoot = 0x004u;  // bool
    public const uint K_TargetPosition = 0x005u;  // float3
    public const uint K_PlayerPosition = 0x006u;  // float3

    // 导航请求
    public const uint K_NavRequestTick = 0x0101u;  // int
    public const uint K_NavTargetPosition = 0x0102u;  // float3
    public const uint K_NavStop = 0x0103u;  // bool
    public const uint K_NavSpeed = 0x0104u;  // float (config)

    // 动画请求
    public const uint K_AnimationRequestId = 0x0201u;  // int (idle / walk / run / shoot)
    public const uint K_AnimationRequestTick = 0x0202u;  // int

    // 射击请求/冷却
    public const uint K_ShootPauseTick = 0x0301u;
    public const uint K_ShootCooldown = 0x0302u;  // float
    public const uint K_ShootCooldownReset = 0x0303u;  // float (config)  

    // 朝向
   public const uint K_lookAtTargetPosition = 0x0401u;  // float3
}