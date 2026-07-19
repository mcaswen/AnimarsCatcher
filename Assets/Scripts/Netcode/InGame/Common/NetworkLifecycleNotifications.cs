using Unity.Collections;
using Unity.Entities;

namespace AnimarsCatcher.Networking
{
    /// <summary>
    /// 标识网络生命周期通知由哪个 World 产生
    /// </summary>
    public enum NetworkNotificationSource
    {
        Unknown = 0,
        ServerWorld,
        ClientWorld
    }

    /// <summary>
    /// 通知表现层有客户端完成大厅身份登记
    /// </summary>
    public struct LobbyClientJoinedNotification : IComponentData
    {
        public NetworkNotificationSource Source;
        public int NetworkId;
        public FixedString64Bytes PlayerName;
        public byte IsLocalPlayer;
    }

    /// <summary>
    /// 通知表现层服务器已经确认对局开始
    /// </summary>
    public struct MatchStartedNotification : IComponentData
    {
        public NetworkNotificationSource Source;
        public int LocalPlayerNetworkId;
    }

    /// <summary>
    /// 请求客户端表现层加载服务器批准的目标场景
    /// </summary>
    public struct ClientSceneLoadRequest : IComponentData
    {
        public FixedString64Bytes SceneName;
    }
}
