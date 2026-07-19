using Unity.Collections;
using Unity.Entities;

namespace AnimarsCatcher.Networking
{
    public enum NetworkNotificationSource
    {
        Unknown = 0,
        ServerWorld,
        ClientWorld
    }

    public struct LobbyClientJoinedNotification : IComponentData
    {
        public NetworkNotificationSource Source;
        public int NetworkId;
        public FixedString64Bytes PlayerName;
        public byte IsLocalPlayer;
    }

    public struct MatchStartedNotification : IComponentData
    {
        public NetworkNotificationSource Source;
        public int LocalPlayerNetworkId;
    }

    public struct ClientSceneLoadRequest : IComponentData
    {
        public FixedString64Bytes SceneName;
    }
}
