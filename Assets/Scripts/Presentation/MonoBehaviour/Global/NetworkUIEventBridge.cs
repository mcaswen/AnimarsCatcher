using System;
using UnityEngine.Events;
using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Presentation.Selection;

namespace AnimarsCatcher.Presentation.Global
{
    /// <summary>
    /// 将不同 NetCode 世界产生的状态变化转发给主线程 UI
    /// Raise 方法负责构造不可变事件载体并统一触发入口
    /// </summary>
    public static class NetworkUIEventBridge
    {
        // 房间成员变化事件
        public static UnityEvent<LobbyClientJoinedEventData> LobbyClientJoinedEvent = new UnityEvent<LobbyClientJoinedEventData>();
        public static UnityEvent<LobbyClientLeftEventData> LobbyClientLeftEvent = new UnityEvent<LobbyClientLeftEventData>();

        // 对局生命周期事件
        public static UnityEvent<MatchStartedEventData> MatchStartedEvent = new UnityEvent<MatchStartedEventData>();
        public static UnityEvent<MatchEndedEventData> MatchEndedEvent = new UnityEvent<MatchEndedEventData>();

        // 玩法和输入状态事件
        public static UnityEvent<SpawnBlasterAniRequestedEventData> SpawnBlasterAniRequestedEvent = new UnityEvent<SpawnBlasterAniRequestedEventData>();
        public static UnityEvent<ResourceChangedRequestedEventData> ResourceChangedRequestedEvent = new UnityEvent<ResourceChangedRequestedEventData>();
        public static UnityEvent<UIPanelInputToggleEvent> UIPanelInputToggleEvent= new UnityEvent<UIPanelInputToggleEvent>();
        public static UnityEvent<AniSelectionModeChangedEvent> AniSelectionModeChanged = new UnityEvent<AniSelectionModeChangedEvent>();

        // 网络连接异常事件
        public static UnityEvent<ConnectionLostEventData> ConnectionLostEvent = new UnityEvent<ConnectionLostEventData>();

#region 对外 Raise 封装

        /// <summary>
        /// 发布房间成员加入事件
        /// </summary>
        public static void RaiseLobbyClientJoinedEvent(
            NetworkUIEventSource source,
            int networkId,
            string playerName,
            bool isLocalPlayer)
        {
            LobbyClientJoinedEvent?.Invoke(
                new LobbyClientJoinedEventData(source, networkId, playerName, isLocalPlayer)
            );
        }

        /// <summary>
        /// 发布房间成员离开事件
        /// </summary>
        public static void RaiseLobbyClientLeftEvent(
            NetworkUIEventSource source,
            int networkId,
            string playerName)
        {
            LobbyClientLeftEvent?.Invoke(
                new LobbyClientLeftEventData(source, networkId, playerName)
            );
        }

        /// <summary>
        /// 发布对局开始事件
        /// </summary>
        public static void RaiseMatchStartedEvent(
            NetworkUIEventSource source,
            int localPlayerNetworkId)
        {
            MatchStartedEvent?.Invoke(
                new MatchStartedEventData(source, localPlayerNetworkId)
            );
        }

        /// <summary>
        /// 发布对局结束事件
        /// </summary>
        public static void RaiseMatchEndedEvent(
            NetworkUIEventSource source,
            string reason)
        {
            MatchEndedEvent?.Invoke(
                new MatchEndedEventData(source, reason)
            );
        }

        /// <summary>
        /// 发布 Blaster Ani 生成请求
        /// </summary>
        public static void RaiseSpawnBlasterAniRequestedEvent(
            NetworkUIEventSource source,
            int requestedCount = 1)
        {
            SpawnBlasterAniRequestedEvent?.Invoke(
                new SpawnBlasterAniRequestedEventData(source, requestedCount)
            );
        }

        /// <summary>
        /// 发布资源变更请求
        /// </summary>
        public static void RaiseResourceChangedRequestedEvent(
            NetworkUIEventSource source,
            ResourceItemKind resourceType,
            int amount)
        {
            ResourceChangedRequestedEvent?.Invoke(
                new ResourceChangedRequestedEventData(source, resourceType, amount)
            );
        }

        /// <summary>
        /// 增加一层 UI 输入锁
        /// </summary>
        public static void RaiseUIPanelInputLocked()
        {
            UIPanelInputToggleEvent?.Invoke(new UIPanelInputToggleEvent
            {
                Delta = +1
            });
        }

        /// <summary>
        /// 释放一层 UI 输入锁
        /// </summary>
        public static void RaiseUIPanelInputUnlocked()
        {
            UIPanelInputToggleEvent?.Invoke(new UIPanelInputToggleEvent
            {
                Delta = -1
            });
        }

        /// <summary>
        /// 发布 Ani 选择模式变化
        /// </summary>
        public static void RaiseAniSelectionModeChanged(AniSelectionMode mode)
        {
            AniSelectionModeChanged?.Invoke(new AniSelectionModeChangedEvent(mode));
        }

        /// <summary>
        /// 发布连接中断事件
        /// </summary>
        public static void RaiseConnectionLostEvent(
            NetworkUIEventSource source,
            int networkId,
            string reason)
        {
            ConnectionLostEvent?.Invoke(
                new ConnectionLostEventData(source, networkId, reason)
            );
        }
    }

#endregion

}
