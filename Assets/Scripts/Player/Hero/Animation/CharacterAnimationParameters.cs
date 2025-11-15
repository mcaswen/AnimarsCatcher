using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

public struct AvatarAnimationParameters : IComponentData
{
    [GhostField]
    public float Speed;     
}
