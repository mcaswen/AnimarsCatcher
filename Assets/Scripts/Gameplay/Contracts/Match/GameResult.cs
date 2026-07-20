using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay.Contracts
{
/// <summary>
/// 保存服务器权威的对局结束状态和胜利阵营
/// </summary>
[GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
public struct GameResult : IComponentData
{
    [GhostField] public byte IsGameOver; // 0=未结束，1=结束
    [GhostField] public CampType Winner;     // 胜利阵营
}

/// <summary>
/// 由服务器发送给每个客户端的对局结束通知
/// </summary>
public struct MatchResultRpc : IRpcCommand
{
    public CampType Winner;
}
}
