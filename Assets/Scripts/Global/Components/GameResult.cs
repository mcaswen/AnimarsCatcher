using Unity.Entities;
using Unity.NetCode;

[GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
public struct GameResult : IComponentData
{
    [GhostField] public byte IsGameOver; // 0=未结束，1=结束
    [GhostField] public CampType Winner;     // 胜利阵营
}

public struct GameOverRpc : IRpcCommand
{
    public CampType Winner;
}
