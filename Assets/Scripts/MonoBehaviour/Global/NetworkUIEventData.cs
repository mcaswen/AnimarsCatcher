using System;
using AnimarsCatcher.Gameplay.Contracts;

namespace AnimarsCatcher.Mono.Global
{
    /// <summary>
    /// 事件产生的 NetCode 世界
    /// </summary>
    public enum NetworkUIEventSource
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
        public readonly NetworkUIEventSource Source;
        public readonly int NetworkId;         // -1 表示当前无法取得网络标识
        public readonly string PlayerName;
        public readonly bool IsLocalPlayer;    // 是否为当前进程控制的玩家

        public LobbyClientJoinedEventData(
            NetworkUIEventSource source,
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
        public readonly NetworkUIEventSource Source;
        public readonly int NetworkId;
        public readonly string PlayerName;

        public LobbyClientLeftEventData(NetworkUIEventSource source, int networkId, string playerName)
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
        public readonly NetworkUIEventSource Source;
        public readonly int LocalPlayerNetworkId;

        public MatchStartedEventData(NetworkUIEventSource source, int localPlayerNetworkId)
        {
            Source = source;
            LocalPlayerNetworkId = localPlayerNetworkId;
        }
    }

    /// <summary>
    /// 对局结束事件载体
    /// </summary>
    public readonly struct MatchEndedEventData
    {
        public readonly NetworkUIEventSource Source;
        public readonly string Reason; // 使用 HostExit AllDead Timeout 等稳定原因码

        public MatchEndedEventData(NetworkUIEventSource source, string reason)
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
        public readonly NetworkUIEventSource Source;
        public readonly int NetworkId;
        public readonly string Reason;

        public ConnectionLostEventData(NetworkUIEventSource source, int networkId, string reason)
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
        public NetworkUIEventSource Source { get; }
        public int RequestedCount { get; }

        public SpawnBlasterAniRequestedEventData(NetworkUIEventSource source, int requestedCount = 1)
        {
            Source = source;
            RequestedCount = requestedCount;
        }
    }

    /// <summary>
    /// 资源变化请求的 UI 事件载体
    /// </summary>
    public readonly struct ResourceChangedRequestedEventData
    {
        public NetworkUIEventSource Source { get; }
        public ResourceItemKind ResourceType { get; }
        public int Amount { get; }

        public ResourceChangedRequestedEventData(
            NetworkUIEventSource source,
            ResourceItemKind resourceType,
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
