namespace AnimarsCatcher.Networking
{
    using Unity.NetCode;
    using Unity.Entities;
    using Unity.Collections;

    /// <summary>
    /// 请求服务器将编辑器调试连接标记为 InGame
    /// </summary>
    public struct DebugEnterGameRpc : IRpcCommand {}

    /// <summary>
    /// 由客户端向服务器提交大厅玩家身份
    /// </summary>
    public struct LobbyIntroRequestRpc : IRpcCommand
    {
        public FixedString64Bytes PlayerName;
    }

    /// <summary>
    /// 由 Host 客户端请求服务器开始指定场景的对局
    /// </summary>
    public struct StartMatchRequestRpc : IRpcCommand
    {
        public FixedString64Bytes SceneName;
    }

    /// <summary>
    /// 由服务器广播给客户端的正式开局通知
    /// </summary>
    public struct StartMatchNotificationRpc : IRpcCommand
    {
        public FixedString64Bytes SceneName;
    }

    /// <summary>
    /// 由场景资源就绪的客户端请求服务器创建角色
    /// </summary>
    public struct ClientReadyForGameRpc : IRpcCommand
    {
    }

    /// <summary>
    /// 标记客户端已收到服务器开局通知
    /// </summary>
    public struct ClientMatchStartState : IComponentData
    {
        // 0 表示尚未开始，非零表示已进入场景加载流程
        public byte Active;
    }
}
