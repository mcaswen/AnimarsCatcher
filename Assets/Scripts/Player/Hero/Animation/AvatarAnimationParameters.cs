using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

/// <summary>保存需要同步到角色表现层的动画参数</summary>
public struct AvatarAnimationParameters : IComponentData
{
    /// <summary>角色表现层使用的移动速度</summary>
    [GhostField]
    public float Speed;     
}
