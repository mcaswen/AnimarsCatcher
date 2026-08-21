namespace AnimarsCatcher.Player
{
    using UnityEngine;
    using Unity.Entities;
    using Unity.NetCode;
    using UnityEngine.Serialization;

    /// <summary>
    /// 标记由玩家输入驱动的控制 Entity
    /// </summary>
    public struct PlayerTag : IComponentData {}

    /// <summary>
    /// 配置玩家控制 Entity 与默认相机的关联
    /// </summary>
    [DisallowMultipleComponent]
    public class ThirdPersonPlayerControlAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("controlledCamera")]
        [SerializeField] private GameObject _controlledCamera;

        private sealed class Baker : Baker<ThirdPersonPlayerControlAuthoring>
        {
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
}
