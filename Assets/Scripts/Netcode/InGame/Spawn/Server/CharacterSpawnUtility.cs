namespace AnimarsCatcher.Networking
{
    using AnimarsCatcher.Gameplay.Contracts;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.Transforms;
    using Unity.NetCode;

    /// <summary>
    /// 提供服务器权威角色实例化和出生点选择逻辑
    /// </summary>
    public static class CharacterSpawnUtility
    {
        /// <summary>
        /// 实例化角色并写入初始姿态、所有者和阵营
        /// </summary>
        /// <param name="entityCommandBuffer">延迟结构变更命令缓冲区</param>
        /// <param name="prefab">角色 Ghost Prefab</param>
        /// <param name="ownerNetworkId">角色所有者的 NetworkId</param>
        /// <param name="position">出生位置</param>
        /// <param name="rotation">出生旋转</param>
        /// <param name="camp">服务器分配阵营</param>
        /// <param name="scale">初始缩放</param>
        /// <returns>新创建的角色实体</returns>
        public static Entity InstantiateAndInitialize(
            ref EntityCommandBuffer entityCommandBuffer,
            Entity prefab,
            int ownerNetworkId,
            in float3 position,
            in quaternion rotation,
            in CampType camp,
            float scale = 1f)
        {
            var character = entityCommandBuffer.Instantiate(prefab);

            entityCommandBuffer.SetComponent(character, LocalTransform.FromPositionRotationScale(position, rotation, scale));
            entityCommandBuffer.AddComponent(character, new GhostOwner { NetworkId = ownerNetworkId });
            entityCommandBuffer.AddComponent(character, new Camp { Value = camp });

            return character;
        }

        /// <summary>
        /// 按配置策略为连接选择出生点
        /// </summary>
        /// <param name="stateRW">出生点轮询状态</param>
        /// <param name="connectionAspect">待分配出生点的连接</param>
        /// <param name="pointsRO">可选出生点列表</param>
        /// <param name="mode">出生点选择策略</param>
        /// <param name="spawnPosition">输出出生位置</param>
        /// <param name="spawnRotation">输出出生旋转</param>
        /// <returns>是否存在可用出生点</returns>
        public static bool TrySelectCharacterSpawnPoint(
            CharacterSpawnPointsState stateRW,
            in ServerGetConnectionAspect connectionAspect,
            in DynamicBuffer<CharacterSpawnPointElement> pointsRO,
            SpawnSelectMode mode,
            out float3 spawnPosition,
            out quaternion spawnRotation)
        {
            spawnPosition = default;
            spawnRotation = quaternion.identity;

            if (pointsRO.Length > 0)
            {
                int index;
                if (mode == SpawnSelectMode.NetworkIdModulo)
                {
                    index = math.abs(connectionAspect.Id) % pointsRO.Length;
                }
                else // 轮询模式从状态记录的下一索引开始选择
                {
                    var currentIndex = stateRW.NextIndex;
                    index = (currentIndex >= 0 ? currentIndex : 0) % pointsRO.Length;
                    stateRW.NextIndex = (index + 1) % pointsRO.Length;
                }

                var point = pointsRO[index];
                spawnPosition = point.Position;
                spawnRotation = point.Rotation;

                return true;
            }

            return false;
        }


    }
}
