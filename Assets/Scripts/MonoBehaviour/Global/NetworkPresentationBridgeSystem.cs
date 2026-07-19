using AnimarsCatcher.Networking;
using AnimarsCatcher.Player;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimarsCatcher.Mono.Global
{
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
                NetworkUIEventBridge.RaiseLobbyClientJoinedEvent(
                    ToUIEventSource(notification.ValueRO.Source),
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
                NetworkUIEventBridge.RaiseMatchStartedEvent(
                    ToUIEventSource(notification.ValueRO.Source),
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

        private static NetworkUIEventSource ToUIEventSource(NetworkNotificationSource source)
        {
            return source switch
            {
                NetworkNotificationSource.ServerWorld => NetworkUIEventSource.ServerWorld,
                NetworkNotificationSource.ClientWorld => NetworkUIEventSource.ClientWorld,
                _ => NetworkUIEventSource.Unknown
            };
        }
    }
}
