using Unity.Entities;
using Unity.NetCode;

[GhostComponent]
public struct Health : IComponentData
{
    [GhostField]
    public int current;

    [GhostField]
    public int max;
}
