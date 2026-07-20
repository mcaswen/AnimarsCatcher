namespace AnimarsCatcher.Player
{
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.Transforms;
    using UnityEngine;
    using UnityEngine.Serialization;

    /// <summary>
    /// 配置立方体测试角色的移动速度
    /// </summary>
    public class CubeAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("moveSpeed")]
        [Range(0.1f, 20f)]
        [SerializeField] private float _moveSpeed = 4f;

        private sealed class Baker : Unity.Entities.Baker<CubeAuthoring>
        {
            public override void Bake(CubeAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponent<PlayerTag>(entity);
                AddComponent(entity, new MoveSpeed { Value = authoring._moveSpeed });

                // 输入命令按网络 Tick 缓冲以支持预测回滚
                AddBuffer<InputCommand>(entity);
            }
        }
    }

    /// <summary>
    /// 保存角色移动速度
    /// </summary>
    public struct MoveSpeed : IComponentData { public float Value; }
}
