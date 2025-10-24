using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.NetCode;


// 角色实例化/初始化, 用 EntityCommandBuffer 操作新建实体
public static class CharacterSpawnUtil
{
    public static Entity InstantiateAndInit(
        ref EntityCommandBuffer entityCommandBuffer,
        Entity prefab,
        int ownerNetworkId,
        in float3 position,
        in quaternion rotation,
        float scale = 1f)
    {
        var character = entityCommandBuffer.Instantiate(prefab);

        entityCommandBuffer.SetComponent(character, LocalTransform.FromPositionRotationScale(position, rotation, scale));
        entityCommandBuffer.AddComponent(character, new GhostOwner { NetworkId = ownerNetworkId });

        return character;
    }

    public static void SelectCharacterSpwanPoint(
        int playerId,
        PlayerSpawnPointsState stateRW,
        in ServerGetConnectionAspect connectionAspect,
        in DynamicBuffer<PlayerSpawnPointElement> pointsRO,
        SpawnSelectMode mode,
        out float3 spawnPosition,
        out quaternion spawnRotation)
    {
        if (pointsRO.Length > 0)
        {
            int idx;
            if (mode == SpawnSelectMode.NetworkIdModulo)
            {
                idx = math.abs(connectionAspect.Id) % pointsRO.Length;
            }
            else // RoundRobin
            {
                var cur = stateRW.NextIndex;
                idx = (cur >= 0 ? cur : 0) % pointsRO.Length;
                stateRW.NextIndex = (idx + 1) % pointsRO.Length;
            }

            var point = pointsRO[idx];
            spawnPosition = point.Position;
            spawnRotation = point.Rotation;
        }
        else
        {
            // 没配置点时，刷新在原点
            spawnPosition = new float3(0, 0.5f, 0);
            spawnRotation = quaternion.identity;

            UnityEngine.Debug.LogWarning("[Server] No PlayerSpawnPointElement found, spawn at (0,0.5,0).");
        }
    }


}
