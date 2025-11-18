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
        in CampType camp,
        float scale = 1f)
    {
        var character = entityCommandBuffer.Instantiate(prefab);

        entityCommandBuffer.SetComponent(character, LocalTransform.FromPositionRotationScale(position, rotation, scale));
        entityCommandBuffer.AddComponent(character, new GhostOwner { NetworkId = ownerNetworkId });
        entityCommandBuffer.AddComponent(character, new Camp { Value = camp });

        return character;
    }

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
        
        return false;
    }


}
