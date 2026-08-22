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
    /// 将网络程序集产生的一次性状态通知转交给客户端表现层
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial class NetworkPresentationBridgeSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            // 通知 Entity 在查询结束后统一销毁，避免遍历期间结构变更
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
            // 玩家加入通知转成表现事件，UI 不直接依赖 Networking 组件
            foreach (var (notification, entity) in SystemAPI
                         .Query<RefRO<LobbyClientJoinedNotification>>()
                         .WithEntityAccess())
            {
                // FixedString 在销毁通知 Entity 前转换为托管字符串
                NetworkPresentationEvents.RaiseLobbyClientJoined(
                    ToNetworkEventSource(notification.ValueRO.Source),
                    notification.ValueRO.NetworkId,
                    notification.ValueRO.PlayerName.ToString(),
                    notification.ValueRO.IsLocalPlayer != 0);
                entityCommandBuffer.DestroyEntity(entity);
            }

            // Host 的服务端表现也需要收到比赛开始通知
            ForwardMatchStartedNotifications(ref entityCommandBuffer);
        }

        private void ForwardClientNotifications(ref EntityCommandBuffer entityCommandBuffer)
        {
            ForwardMatchStartedNotifications(ref entityCommandBuffer);

            // 场景加载请求保持一次性语义，不能在后续帧重复发起加载
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
                // 事件携带来源 World 和本地玩家编号，供 UI 判断上下文
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
            // 桥接层显式映射枚举，避免表现程序集引用网络内部类型
            return source switch
            {
                NetworkNotificationSource.ServerWorld => NetworkEventSource.ServerWorld,
                NetworkNotificationSource.ClientWorld => NetworkEventSource.ClientWorld,
                _ => NetworkEventSource.Unknown
            };
        }
    }
}
