using Unity.Entities;
using Unity.Mathematics;
using System;
using Unity.NetCode;

/// <summary>
/// 保存固定相机在客户端和服务器间同步的配置
/// </summary>
[Serializable]
[GhostComponent]
public struct FixedCamera : IComponentData
{
    /// <summary>
    /// 相机与跟随目标的距离
    /// </summary>
    [GhostField]
    public float Distance;     // 相机距离

    /// <summary>
    /// 固定视角俯仰角
    /// </summary>
    [GhostField]
    public float PitchDeg;     // 俯仰角

    /// <summary>
    /// 固定视角偏航角
    /// </summary>
    [GhostField]
    public float YawDeg;       // 偏航角

    /// <summary>
    /// 相机相对目标的高度偏移
    /// </summary>
    [GhostField]
    public float Height;       // 相机本体额外抬高

    /// <summary>
    /// 位置变化的阻尼时长
    /// </summary>
    [GhostField]
    public float Damping;      // 位置阻尼

    /// <summary>
    /// 观察点相对角色的高度偏移
    /// </summary>
    [GhostField]
    public float LookUpBias;   // 观察点额外抬高

    // 网络状态发生较大偏差时直接吸附，避免阻尼追赶造成长时间错位
    /// <summary>
    /// 触发位置直接吸附的距离阈值
    /// </summary>
    [GhostField]
    public float SnapDistance;   // 位置 snap 距离阈值
    
    /// <summary>
    /// 触发旋转直接吸附的角度阈值
    /// </summary>
    [GhostField]
    public float SnapAngleDeg;   // 旋转 snap 角度阈值

}

/// <summary>
/// 保存固定相机当前跟随的角色实体
/// </summary>
[Serializable]
public struct FixedCameraControl : IComponentData
{
    [GhostField]
    public Entity FollowedCharacterEntity;
}

/// <summary>
/// 保存固定相机阻尼计算所需的跨帧速度
/// </summary>
[Serializable]
public struct FixedCameraSmoothState : IComponentData
{
    [GhostField]
    public float3 Velocity;
}
