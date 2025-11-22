using Unity.NetCode;
using Unity.Entities;

public enum ResourceItemKind : byte
{
    Food   = 0,
    Crystal = 1,
}

[GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
public struct ResourceItemTag : IComponentData
{}
