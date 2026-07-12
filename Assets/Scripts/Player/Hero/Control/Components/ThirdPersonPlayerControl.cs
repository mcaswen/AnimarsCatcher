using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

/// <summary>保存玩家控制实体当前绑定的角色和相机</summary>
[GhostComponent]
public struct ThirdPersonPlayerControl : IComponentData
{
    /// <summary>当前接收玩家输入的角色实体</summary>
    [GhostField]
    public Entity ControlledCharacter;
    
    /// <summary>当前由玩家输入驱动的相机实体</summary>
    [GhostField]
    public Entity ControlledCamera;
}
