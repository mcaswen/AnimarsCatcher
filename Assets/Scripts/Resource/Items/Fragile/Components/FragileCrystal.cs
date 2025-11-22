using Unity.Entities;
using Unity.NetCode;

[GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
public struct FragileCrystal : IComponentData
{
     [GhostField]
    public ResourceItemKind DropKind;

    // 掉落资源数量
     [GhostField]
    public int TotalDropResourceAmount;

    // 掉落几个小矿实体（比如 5 个）
     [GhostField]
    public int DropPieceCount;

    // 掉落小矿的随机半径
     [GhostField]
    public float DropSpawnRadius;

    // 生成用的小矿预制体
    public Entity PickablePrefab;
}

[GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
public struct AttackableResourceTag : IComponentData {}
