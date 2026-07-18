using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay.Contracts
{
/// <summary>
/// 标记允许进入近战伤害结算链路的实体
/// </summary>
[GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
public struct MeleeAttackableTag : IComponentData {}

/// <summary>
/// 标记允许进入远程伤害结算链路的实体
/// </summary>
[GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
public struct RangedAttackableTag : IComponentData {}
}
