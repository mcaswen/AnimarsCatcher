using AnimarsCatcher.Networking;
using AnimarsCatcher.Player;
using AnimarsCatcher.Presentation.UI;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimarsCatcher.Presentation.Network
{
    /// <summary>
    /// 将网络程序集发布的短生命周期数据通知转交给客户端表现层
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial class NetworkPresentationBridgeSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            if (World.IsServer())
            {
                ForwardServerNotifications(ref entityCommandBuffer);
            }

            if (World.IsClient())
            {
                ForwardClientNotifications(ref entityCommandBuffer);
            }

            entityCommandBuffer.Playback(EntityManager);
            entityCommandBuffer.Dispose();
        }

        private void ForwardServerNotifications(ref EntityCommandBuffer entityCommandBuffer)
        {
            foreach (var (notification, entity) in SystemAPI
                         .Query<RefRO<LobbyClientJoinedNotification>>()
                         .WithEntityAccess())
            {
                NetworkPresentationEvents.RaiseLobbyClientJoined(
                    ToNetworkEventSource(notification.ValueRO.Source),
                    notification.ValueRO.NetworkId,
                    notification.ValueRO.PlayerName.ToString(),
                    notification.ValueRO.IsLocalPlayer != 0);
                entityCommandBuffer.DestroyEntity(entity);
            }

            ForwardMatchStartedNotifications(ref entityCommandBuffer);
        }

        private void ForwardClientNotifications(ref EntityCommandBuffer entityCommandBuffer)
        {
            ForwardMatchStartedNotifications(ref entityCommandBuffer);

            foreach (var (request, entity) in SystemAPI
                         .Query<RefRO<ClientSceneLoadRequest>>()
                         .WithEntityAccess())
            {
                StartClientSceneLoad(request.ValueRO.SceneName.ToString());
                entityCommandBuffer.DestroyEntity(entity);
            }
        }

        private void ForwardMatchStartedNotifications(ref EntityCommandBuffer entityCommandBuffer)
        {
            foreach (var (notification, entity) in SystemAPI
                         .Query<RefRO<MatchStartedNotification>>()
                         .WithEntityAccess())
            {
                NetworkPresentationEvents.RaiseMatchStarted(
                    ToNetworkEventSource(notification.ValueRO.Source),
                    notification.ValueRO.LocalPlayerNetworkId);
                entityCommandBuffer.DestroyEntity(entity);
            }
        }

        private static void StartClientSceneLoad(string sceneName)
        {
            if (GlobalLoadingUI.Instance != null)
            {
                GlobalLoadingUI.Instance.StartLoadingAndTransition(sceneName);
                return;
            }

            // 加载界面缺失时仍完成协议要求的场景切换
            Debug.LogWarning("[NetworkPresentationBridgeSystem] GlobalLoadingUI is missing, load scene directly");
            SceneManager.LoadScene(sceneName);
            ClientCinematicState.ShouldRunIntro = true;
        }

        private static NetworkEventSource ToNetworkEventSource(NetworkNotificationSource source)
        {
            return source switch
            {
                NetworkNotificationSource.ServerWorld => NetworkEventSource.ServerWorld,
                NetworkNotificationSource.ClientWorld => NetworkEventSource.ClientWorld,
                _ => NetworkEventSource.Unknown
            };
        }
    }
}
