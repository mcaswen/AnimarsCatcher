using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Entities;
using System;

/// <summary>标记需要驱动主 GameObject 相机的实体</summary>
[Serializable]
public struct MainEntityCamera : IComponentData {}

/// <summary>把场景中的相机目标烘焙为主相机实体</summary>
[DisallowMultipleComponent]
public class MainEntityCameraAuthoring : MonoBehaviour
{
    /// <summary>负责创建主相机标记组件</summary>
    public class Baker : Baker<MainEntityCameraAuthoring>
    {
        /// <summary>将 Authoring 配置写入实体</summary>
        /// <param name="authoring">主相机 Authoring 配置</param>
        public override void Bake(MainEntityCameraAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent<MainEntityCamera>(entity);
        }
    }
}
