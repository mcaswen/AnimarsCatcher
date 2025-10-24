using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

public struct CharacterAnimationComponent : IComponentData
{
    [GhostField]
    public float Speed;

    [GhostField]
    public bool Grounded; 

    [GhostField]
    public float3 Move;     
}
