using Unity.NetCode;
using Unity.Entities;

namespace AnimarsCatcher.Gameplay.Contracts
{
/// <summary>
/// 可收集资源实体的玩法类别
/// </summary>
public enum ResourceItemKind : byte
{
    Food   = 0,
    Crystal = 1,
}

/// <summary>
/// 标识需要同步给所有客户端的资源实体
/// </summary>
[GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
public struct ResourceItemTag : IComponentData
{}
}
