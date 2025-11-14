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
        CharacterSpawnPointsState stateRW,
        in ServerGetConnectionAspect connectionAspect,
        in DynamicBuffer<CharacterSpawnPointElement> pointsRO,
        SpawnSelectMode mode,
        out float3 spawnPosition,
        out quaternion spawnRotation)
    {
        if (pointsRO.Length > 0)
        {
            int index;
            if (mode == SpawnSelectMode.NetworkIdModulo)
            {
                index = math.abs(connectionAspect.Id) % pointsRO.Length;
            }
            else // RoundRobin
            {
                var currentIndex = stateRW.NextIndex;
                index = (currentIndex >= 0 ? currentIndex : 0) % pointsRO.Length;
                stateRW.NextIndex = (index + 1) % pointsRO.Length;
            }

            var point = pointsRO[index];
            spawnPosition = point.Position;
            spawnRotation = point.Rotation;
        }
        else
        {
            // 没配置点时，刷新在原点
            spawnPosition = new float3(0, 0.5f, 0);
            spawnRotation = quaternion.identity;

            UnityEngine.Debug.LogWarning("[Server Spawner] No PlayerSpawnPointElement found, spawn at (0,0.5,0).");
        }
    }


}
