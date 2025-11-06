//using Unity.Android.Gradle.Manifest;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

public static class BlasterAniIDs
{
    public const ushort kStateOffset = 0;
    public const ushort kCondOffset  = 64;
    public const ushort kActOffset   = 128;

    // StateId， 0 预留给通用 S0，这里从 1 开始
    public readonly static ushort S_Idle = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kStateOffset + 1);
    public readonly static ushort S_Follow = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kStateOffset + 2);
    public readonly static ushort S_Find  = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kStateOffset + 3);
    public readonly static ushort S_Shoot = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kStateOffset + 4);


    // ConditionId
    public readonly static ushort C_ShouldFollow = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kCondOffset + 1); // IsFollowingPlayer == true
    public readonly static ushort C_ShouldFind = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kCondOffset + 2); // IsFinding == true && TargetValid && !CanShoot
    public readonly static ushort C_ShouldShoot = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kCondOffset + 3); // IsFinding == true && TargetValid && CanShoot
    public readonly static ushort C_TargetGone = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kCondOffset + 4); // !TargetValid

    // ActionId
    public readonly static ushort A_EnterFollow = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kActOffset + 1);
    public readonly static ushort A_ExitFollow = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kActOffset + 2);
    public readonly static ushort A_EnterFind = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kActOffset + 3);
    public readonly static ushort A_ExitFind = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kActOffset + 4);
    public readonly static ushort A_EnterShoot = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kActOffset + 5);
    public readonly static ushort A_ExitShoot = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kActOffset + 6);
    public readonly static ushort A_EnterIdle = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kActOffset + 7);
    public readonly static ushort A_ExitIdle = FsmIdSpace.Of(FsmIdSpace.BlasterAniBase, kActOffset + 8);
}

public static class BlasterAniBlackBoardKeys
{
    // 外部输入命令
    public const uint K_IsFollowing = 0x001u;  // bool 外部系统控制，驱动状态切换
    public const uint K_IsFinding = 0x002u;  // bool 外部系统控制，驱动状态切换

    // 感知/目标
    public const uint K_HasLOS = 0x004u;  // bool
    public const uint K_TargetEntity = 0x005u;  // Entity

    // 导航请求
    public const uint K_NavRequestVersion = 0x0101u;  // int 为去抖，版本号变化时才下发 SetDestination
    public const uint K_NavTargetPosition = 0x0102u;  // float3
    public const uint K_NavStop = 0x0103u;  // bool 

    // 动画请求
    public const uint K_AnimationPlayRequestId = 0x0201u;  // int (idle / walk / run / shoot)
    public const uint K_AnimationRequestVersion = 0x0202u;  // int

    // 射击请求/冷却
    public const uint K_CanFire = 0x0301u; // bool（距离 + LOS + 冷却合成）
    public const uint K_NextFireTick = 0x0302u;  // int（下一次射击的 Tick）
   
    // 开火事件
    public const uint K_FireEventVersion = 0x0140u; // int，每次开火事件版本号加一，外部消费
   
}