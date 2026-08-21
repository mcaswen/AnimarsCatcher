using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay.Contracts
{
/// <summary>
    /// 标记允许参与近战伤害结算的 Entity
/// </summary>
[GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
public struct MeleeAttackableTag : IComponentData {}

/// <summary>
    /// 标记允许参与远程伤害结算的 Entity
/// </summary>
[GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
public struct RangedAttackableTag : IComponentData {}
}
