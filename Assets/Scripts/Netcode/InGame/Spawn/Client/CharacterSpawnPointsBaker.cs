namespace AnimarsCatcher.Networking
{
    using AnimarsCatcher.Gameplay.Contracts;
    using Unity.Entities;
    using UnityEngine;

    /// <summary>
    /// 负责把子节点姿态烘焙为角色出生点缓冲区
    /// </summary>
    public class CharacterSpawnPointsBaker : Baker<CharacterSpawnPointsAuthoring>
    {
        public override void Bake(CharacterSpawnPointsAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);

            AddComponent<CharacterSpawnPointsTag>(entity);
            AddComponent(entity, new CharacterSpawnPointsState { NextIndex = 0 });
            AddComponent(entity, new CharacterSpawnSelectMode { Value = authoring.selectMode });
            AddComponent(entity, new Camp { Value = authoring.campType });

            var buffer = AddBuffer<CharacterSpawnPointElement>(entity);

            var points = authoring.gameObject.GetComponentsInChildren<Transform>();
            foreach (var point in points)
            {
                if (point == authoring.transform) continue;

                buffer.Add(new CharacterSpawnPointElement
                {
                    Position = point.position,
                    Rotation = point.rotation
                });
            }
        }
    }
}
