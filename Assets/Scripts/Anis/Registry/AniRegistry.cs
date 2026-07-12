using Unity.Entities;
using UnityEngine;

/// <summary>
/// 在 SubScene 中注册可由服务器实例化的 Ani Ghost 预制体
/// </summary>
public class AniRegistry : MonoBehaviour
{
    public GameObject BlasterAniGhostPrefab;
    public GameObject PickerAniGhostPrefab;

    /// <summary>
    /// 将预制体引用转换为实体注册表
    /// </summary>
    class Baker : Baker<AniRegistry>
    {
        /// <summary>
        /// 烘焙 Blaster 和 Picker 的 Ghost 预制体实体引用
        /// </summary>
        /// <param name="authoring">Ani 预制体注册组件</param>
        public override void Bake(AniRegistry authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            var blasterPrefabEntity = GetEntity(authoring.BlasterAniGhostPrefab, TransformUsageFlags.Dynamic);
            var pickerPrefabEntity = GetEntity(authoring.PickerAniGhostPrefab, TransformUsageFlags.Dynamic);

            AddComponent(entity, new AniGhostPrefabCollection
            {
                BlasterAniPrefabEntity = blasterPrefabEntity,
                PickerAniPrefabEntity = pickerPrefabEntity
            });
        }
    }
}

/// <summary>
/// 保存服务器生成系统使用的两类 Ani Ghost 预制体实体
/// </summary>
public struct AniGhostPrefabCollection : IComponentData
{
    public Entity BlasterAniPrefabEntity;
    public Entity PickerAniPrefabEntity;
}
