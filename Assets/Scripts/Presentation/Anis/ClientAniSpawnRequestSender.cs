using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using AnimarsCatcher.Gameplay;
using AnimarsCatcher.Networking;

namespace AnimarsCatcher.Presentation.Anis
{
    /// <summary>
    /// 从客户端世界创建 Ani 生成请求并发送给服务器
    /// </summary>
    public static class ClientAniSpawnRequestSender
    {
        /// <summary>
        /// 请求服务器为当前连接生成指定数量的两类 Ani
        /// </summary>
        /// <param name="blasterAniSpawnCount">需要生成的 Blaster 数量</param>
        /// <param name="pickerAniSpawnCount">需要生成的 Picker 数量</param>
        public static void RequestSpawnAnis(int blasterAniSpawnCount, int pickerAniSpawnCount)
        {
            var clientWorld = NetworkWorldLocator.FindClientWorld();
            if (clientWorld == null)
            {
                Debug.LogWarning("[ClientAniSpawnRequestSender] No client world, cannot send spawn request.");
                return;
            }

            var entityManager = clientWorld.EntityManager;

            using (var query = entityManager.CreateEntityQuery(typeof(NetworkId)))
            {
                if (query.IsEmpty)
                {
                    Debug.LogWarning("[ClientAniSpawnRequestSender] No NetworkId, client not connected.");
                    return;
                }

                // RPC 必须挂到当前客户端唯一的网络连接
                var connection = query.GetSingletonEntity();

                var rpcEntity = entityManager.CreateEntity();
                entityManager.AddComponentData(rpcEntity, new SpawnAniRequestRpc
                {
                    BlasterAniSpawnCount = blasterAniSpawnCount,
                    PickerAniSpawnCount = pickerAniSpawnCount
                }
                );
                entityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest
                {
                    TargetConnection = connection
                });

                Debug.Log("[ClientAniSpawnRequestSender] SpawnAniRequestRpc sent");
            }
        }
    }
}
