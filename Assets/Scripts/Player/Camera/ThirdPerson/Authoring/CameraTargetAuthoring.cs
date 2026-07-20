namespace AnimarsCatcher.Player
{
    using System.Collections;
    using System.Collections.Generic;
    using UnityEngine;
    using UnityEngine.Serialization;
    using Unity.Entities;

    /// <summary>
    /// 配置相机需要跟随的目标对象
    /// </summary>
    [DisallowMultipleComponent]
    public class CameraTargetAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("Target")]
        [SerializeField] private GameObject _target;

        private sealed class Baker : Baker<CameraTargetAuthoring>
        {
            public override void Bake(CameraTargetAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new CameraTarget
                {
                    TargetEntity = GetEntity(authoring._target, TransformUsageFlags.Dynamic),
                });
            }
        }
    }
}
