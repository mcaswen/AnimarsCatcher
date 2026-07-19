namespace AnimarsCatcher.Player
{
    using System.Collections;
    using System.Collections.Generic;
    using UnityEngine;
    using Unity.Entities;

    /// <summary>
    /// 配置相机需要跟随的目标对象
    /// </summary>
    [DisallowMultipleComponent]
    public class CameraTargetAuthoring : MonoBehaviour
    {
        public GameObject Target;

        /// <summary>
        /// 负责将相机目标引用转换为实体引用
        /// </summary>
        public class Baker : Baker<CameraTargetAuthoring>
        {
            public override void Bake(CameraTargetAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new CameraTarget
                {
                    TargetEntity = GetEntity(authoring.Target, TransformUsageFlags.Dynamic),
                });
            }
        }
    }
}
