using UnityEngine;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine.Serialization;

/// <summary>标记由玩家输入驱动的控制实体</summary>
public struct PlayerTag : IComponentData {}

/// <summary>配置玩家控制实体与默认相机的关联</summary>
[DisallowMultipleComponent]
public class ThirdPersonPlayerControlAuthoring : MonoBehaviour
{
    /// <summary>负责创建玩家输入和控制关系组件</summary>
    [FormerlySerializedAs("controlledCamera")]
    [SerializeField] private GameObject _controlledCamera;

    public class Baker : Baker<ThirdPersonPlayerControlAuthoring>
    {
        /// <summary>烘焙玩家控制实体及其相机引用</summary>
        /// <param name="authoring">玩家控制 Authoring 配置</param>
        public override void Bake(ThirdPersonPlayerControlAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new ThirdPersonPlayerControl
            {
                ControlledCamera = GetEntity(authoring._controlledCamera, TransformUsageFlags.Dynamic)
            });

            AddComponent<PlayerInput>(entity);
            AddComponent<PlayerTag>(entity);
        }
    }
}
