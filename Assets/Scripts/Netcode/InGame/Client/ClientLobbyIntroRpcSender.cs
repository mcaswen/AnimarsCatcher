namespace AnimarsCatcher.Networking
{
    using Unity.Entities;
    using Unity.NetCode;
    using Unity.Collections;
    using UnityEngine;

    /// <summary>
    /// 提供大厅 UI 向服务器发送玩家身份信息的入口
    /// </summary>
    public static class ClientLobbyIntroRpcSender
    {
        /// <summary>
        /// 在指定 Client World 中创建大厅身份 RPC
        /// </summary>
        /// <param name="clientWorld">发送身份信息的客户端世界</param>
        /// <param name="playerName">玩家显示名称</param>
        public static void SendIntro(World clientWorld, string playerName)
        {
            var entityManager = clientWorld.EntityManager;

            // NetworkId 存在表示连接握手完成，此前创建 RPC 不会有有效目标
            var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
            if (query.IsEmpty)
            {
                Debug.LogWarning("[ClientLobbyIntroRpcSender] No NetworkId yet, cannot send intro");
                query.Dispose();
                return;
            }

            var connectionEntity = query.GetSingletonEntity();
            query.Dispose();

            var rpcEntity = entityManager.CreateEntity();
            entityManager.AddComponentData(rpcEntity, new LobbyIntroRequestRpc
            {
                PlayerName = new FixedString64Bytes(playerName)
            });
            entityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest
            {
                TargetConnection = connectionEntity
            });

            Debug.Log($"[ClientLobbyIntroRpcSender] Sent lobby intro as '{playerName}'");
        }
    }
}
