using System;

namespace AnimarsCatcher.Mono.Global
{
    /// <summary>
    /// 事件产生的 NetCode 世界
    /// </summary>
    public enum NetUIEventSource
    {
        Unknown = 0,
        ServerWorld,
        ClientWorld
    }

#region Lobby 相关事件载体

    /// <summary>
    /// 房间成员加入事件载体
    /// </summary>
    public readonly struct LobbyClientJoinedEventData
    {
        public readonly NetUIEventSource Source;
        public readonly int NetworkId;         // -1 表示当前无法取得网络标识
        public readonly string PlayerName;
        public readonly bool IsLocalPlayer;    // 是否为当前进程控制的玩家

        public LobbyClientJoinedEventData(
            NetUIEventSource source,
            int networkId,
            string playerName,
            bool isLocalPlayer)
        {
            Source = source;
            NetworkId = networkId;
            PlayerName = playerName;
            IsLocalPlayer = isLocalPlayer;
        }
    }

    /// <summary>
    /// 房间成员离开或掉线事件载体
    /// </summary>
    public readonly struct LobbyClientLeftEventData
    {
        public readonly NetUIEventSource Source;
        public readonly int NetworkId;
        public readonly string PlayerName;

        public LobbyClientLeftEventData(NetUIEventSource source, int networkId, string playerName)
        {
            Source = source;
            NetworkId = networkId;
            PlayerName = playerName;
        }
    }

#endregion

#region Match 相关事件载体

    /// <summary>
    /// 对局开始事件载体
    /// </summary>
    public readonly struct MatchStartedEventData
    {
        public readonly NetUIEventSource Source;
        public readonly int LocalPlayerNetworkId;

        public MatchStartedEventData(NetUIEventSource source, int localId)
        {
            Source = source;
            LocalPlayerNetworkId = localId;
        }
    }

    /// <summary>
    /// 对局结束事件载体
    /// </summary>
    public readonly struct MatchEndedEventData
    {
        public readonly NetUIEventSource Source;
        public readonly string Reason; // 使用 HostExit AllDead Timeout 等稳定原因码

        public MatchEndedEventData(NetUIEventSource source, string reason)
        {
            Source = source;
            Reason = reason;
        }
    }
#endregion

#region Connection 相关事件载体

    /// <summary>
    /// 网络连接中断事件载体
    /// </summary>
    public readonly struct ConnectionLostEventData
    {
        public readonly NetUIEventSource Source;
        public readonly int NetworkId;
        public readonly string Reason;

        public ConnectionLostEventData(NetUIEventSource source, int networkId, string reason)
        {
            Source = source;
            NetworkId = networkId;
            Reason = reason;
        }
    }

#endregion

#region Gameplay 相关事件载体

    /// <summary>
    /// 请求生成 Blaster Ani 的 UI 事件载体
    /// </summary>
    public readonly struct SpawnBlasterAniRequestedEventData
    {
        public NetUIEventSource Source { get; }
        public int RequestedCount { get; }  

        public SpawnBlasterAniRequestedEventData(NetUIEventSource source, int requestedCount = 1)
        {
            Source = source;
            RequestedCount = requestedCount;
        }
    }

    /// <summary>
    /// UI 可请求调整的资源类型
    /// </summary>
    public enum ResourceType
    {
        Food,
        Crystal
    }

    /// <summary>
    /// 资源变化请求的 UI 事件载体
    /// </summary>
    public readonly struct ResourceChangedRequestedEventData
    {
        public NetUIEventSource Source { get; }
        public ResourceType ResourceType { get; }
        public int Amount { get; }

        public ResourceChangedRequestedEventData(
            NetUIEventSource source,
            ResourceType resourceType,
            int amount)
        {
            Source       = source;
            ResourceType = resourceType;
            Amount       = amount;
        }
    }

    /// <summary>
    /// UI 输入锁计数变化
    /// 使用计数避免多个面板同时开关时提前释放输入
    /// </summary>
    public struct UIPanelInputToggleEvent
    {
        public int Delta;
    }

    /// <summary>
    /// Ani 选择交互模式变化事件
    /// </summary>
    public struct AniSelectionModeChangedEvent
    {
        public AniSelectionMode Mode;

        public AniSelectionModeChangedEvent(AniSelectionMode mode)
        {
            Mode = mode;
        }
    }


#endregion

}
