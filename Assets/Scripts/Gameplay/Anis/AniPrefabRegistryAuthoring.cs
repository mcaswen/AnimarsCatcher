using Unity.Entities;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在 SubScene 中注册可由服务器实例化的 Ani Ghost 预制体
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Gameplay", "AnimarsCatcher.Gameplay", "AniRegistry")]
    public class AniPrefabRegistryAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("BlasterAniGhostPrefab")]
        [SerializeField] private GameObject _blasterAniGhostPrefab;

        [FormerlySerializedAs("PickerAniGhostPrefab")]
        [SerializeField] private GameObject _pickerAniGhostPrefab;

        private sealed class Baker : Baker<AniPrefabRegistryAuthoring>
        {
            public override void Bake(AniPrefabRegistryAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                var blasterPrefabEntity = GetEntity(authoring._blasterAniGhostPrefab, TransformUsageFlags.Dynamic);
                var pickerPrefabEntity = GetEntity(authoring._pickerAniGhostPrefab, TransformUsageFlags.Dynamic);

                AddComponent(entity, new AniGhostPrefabRegistry
                {
                    BlasterAniPrefabEntity = blasterPrefabEntity,
                    PickerAniPrefabEntity = pickerPrefabEntity
                });
            }
        }
    }

    /// <summary>
    /// 保存服务器生成系统使用的两类 Ani Ghost 预制体 Entity
    /// </summary>
    public struct AniGhostPrefabRegistry : IComponentData
    {
        public Entity BlasterAniPrefabEntity;
        public Entity PickerAniPrefabEntity;
    }
}
