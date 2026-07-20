namespace AnimarsCatcher.Player
{
    using System;
    using Unity.Entities;
    using UnityEngine;

    /// <summary>
    /// 标记需要驱动主 GameObject 相机的实体
    /// </summary>
    [Serializable]
    public struct MainEntityCameraTag : IComponentData {}

    /// <summary>
    /// 把场景中的相机目标烘焙为主相机实体
    /// </summary>
    [DisallowMultipleComponent]
    public class MainEntityCameraAuthoring : MonoBehaviour
    {
        private sealed class Baker : Baker<MainEntityCameraAuthoring>
        {
            public override void Bake(MainEntityCameraAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<MainEntityCameraTag>(entity);
            }
        }
    }
}
