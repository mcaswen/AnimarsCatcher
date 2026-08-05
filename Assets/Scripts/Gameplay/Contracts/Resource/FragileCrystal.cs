using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay.Contracts
{
/// <summary>
/// 可破坏水晶的掉落规则和预制体引用
/// </summary>
[GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
public struct FragileCrystal : IComponentData
{
    [GhostField]
    public ResourceItemKind DropKind;

    // 破坏后提供的资源总量
    [GhostField]
    public int TotalDropResourceAmount;

    // 将总量拆分成的可拾取实体数量
    [GhostField]
    public int DropPieceCount;

    // 生成位置相对水晶中心的随机半径
    [GhostField]
    public float DropSpawnRadius;

    // 服务端生成可拾取资源使用的预制体
    public Entity PickablePrefab;
}

/// <summary>
/// 标识可被 Ani 攻击系统选中的资源
/// </summary>
[GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
public struct AttackableResourceTag : IComponentData {}
}
