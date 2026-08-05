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
        // 0 表示进行中，1 表示已经结束
        [GhostField]
        public byte IsGameOver;

        [GhostField]
        public CampType Winner;
    }

    /// <summary>
    /// 由服务器发送给每个客户端的对局结束通知
    /// </summary>
    public struct MatchResultRpc : IRpcCommand
    {
        public CampType Winner;
    }
}
