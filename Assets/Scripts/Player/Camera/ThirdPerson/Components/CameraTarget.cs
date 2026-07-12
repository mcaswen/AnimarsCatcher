using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

/// <summary>
/// 保存相机实际跟随目标的实体引用
/// </summary>
[Serializable]
public struct CameraTarget : IComponentData
{
    /// <summary>
    /// 角色层级中作为相机观察点的实体
    /// </summary>
    [GhostField]
    public Entity TargetEntity;
}
