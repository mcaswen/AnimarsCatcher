namespace AnimarsCatcher.Networking
{
    using AnimarsCatcher.Gameplay.Contracts;
    using Unity.Entities;
    using UnityEngine;
    using UnityEngine.Serialization;

    /// <summary>
    /// 配置某个阵营的出生点集合和选择策略
    /// </summary>
    public class CharacterSpawnPointsAuthoring : MonoBehaviour
    {
        [Tooltip("RoundRobin 轮询，NetworkIdModulo 按连接 ID 取模")]
        [FormerlySerializedAs("selectMode")]
        [SerializeField] private CharacterSpawnSelectionMode _selectionMode =
            CharacterSpawnSelectionMode.RoundRobin;

        [FormerlySerializedAs("campType")]
        [SerializeField] private CampType _camp = CampType.Alpha;

        private sealed class Baker : Unity.Entities.Baker<CharacterSpawnPointsAuthoring>
        {
            public override void Bake(CharacterSpawnPointsAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);

                AddComponent<CharacterSpawnPointsTag>(entity);
                AddComponent(entity, new CharacterSpawnPointsState { NextIndex = 0 });
                AddComponent(entity, new CharacterSpawnSelectionConfig
                {
                    Value = authoring._selectionMode
                });
                AddComponent(entity, new Camp { Value = authoring._camp });

                var buffer = AddBuffer<CharacterSpawnPointElement>(entity);
                var points = authoring.gameObject.GetComponentsInChildren<Transform>();
                foreach (var point in points)
                {
                    if (point == authoring.transform)
                    {
                        continue;
                    }

                    buffer.Add(new CharacterSpawnPointElement
                    {
                        Position = point.position,
                        Rotation = point.rotation
                    });
                }
            }
        }
    }
}
