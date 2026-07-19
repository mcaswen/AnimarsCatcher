namespace AnimarsCatcher.Networking
{
    using Unity.NetCode;
    using Unity.Entities;
    using Unity.Collections;

    /// <summary>
    /// 请求服务器将编辑器调试连接标记为 InGame
    /// </summary>
    public struct GoInGameRequest : IRpcCommand {}

    /// <summary>
    /// 由客户端向服务器提交大厅玩家身份
    /// </summary>
    public struct ClientLobbyIntroRpc : IRpcCommand
    {
        /// <summary>
        /// 客户端提交的大厅显示名称
        /// </summary>
        public FixedString64Bytes PlayerName;
    }

    /// <summary>
    /// 由 Host 客户端请求服务器开始指定场景的对局
    /// </summary>
    public struct StartGameRpc : IRpcCommand
    {
        /// <summary>
        /// Host 请求开始的目标场景
        /// </summary>
        public FixedString64Bytes SceneName;
    }

    /// <summary>
    /// 由服务器广播给客户端的权威开局通知
    /// </summary>
    public struct ClientStartGameRpc : IRpcCommand
    {
        /// <summary>
        /// 服务器确认的权威目标场景
        /// </summary>
        public FixedString64Bytes SceneName;
    }

    /// <summary>
    /// 由场景资源就绪的客户端请求服务器创建角色
    /// </summary>
    public struct SetInGameRpc : IRpcCommand
    {
    }

    /// <summary>
    /// 标记客户端已收到服务器开局通知
    /// </summary>
    public struct ClientMatchStartState : IComponentData
    {
        /// <summary>
        /// 客户端是否已进入开局场景加载阶段
        /// </summary>
        public byte Active;
    }
}
