namespace AnimarsCatcher.Networking
{
    using Unity.Collections;
    using Unity.Entities;

    /// <summary>
    /// 保存服务器开局协议各阶段的单例状态
    /// </summary>
    public struct ServerMatchStartState : IComponentData
    {
        public FixedString64Bytes SceneName;
        public byte MatchStartRequested;
        public byte ClientStartRpcSent;
        public byte CharactersSpawned;
    }
}
