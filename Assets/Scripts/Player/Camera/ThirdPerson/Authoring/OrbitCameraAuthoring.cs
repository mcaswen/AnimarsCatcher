namespace AnimarsCatcher.Player
{
    using System.Collections.Generic;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.Transforms;
    using UnityEngine;
    using UnityEngine.Serialization;

    /// <summary>
    /// 配置环绕相机的旋转、缩放、遮挡和忽略实体
    /// </summary>
    [DisallowMultipleComponent]
    public class OrbitCameraAuthoring : MonoBehaviour
    {
        // 旋转参数控制输入响应以及相对移动平台的朝向继承
        [Header("Rotation")]
        public float RotationSpeed = 2f;
        [FormerlySerializedAs("MaxVAngle")]
        public float MaxVerticalAngle = 89f;
        [FormerlySerializedAs("MinVAngle")]
        public float MinVerticalAngle = -89f;
        public bool RotateWithCharacterParent = true;

        // 距离平滑与输入速度分开配置，避免缩放手感依赖帧率
        [Header("Distance")]
        public float StartDistance = 5f;
        public float MinDistance = 0f;
        public float MaxDistance = 10f;
        public float DistanceMovementSpeed = 1f;
        public float DistanceMovementSharpness = 20f;

        // 遮挡收缩和恢复使用不同平滑强度，减少穿模和相机弹跳
        [Header("Obstructions")]
        public float ObstructionRadius = 0.1f;
        public float ObstructionInnerSmoothingSharpness = float.MaxValue;
        public float ObstructionOuterSmoothingSharpness = 5f;
        public bool PreventFixedUpdateJitter = true;

        // 忽略列表用于排除角色附件等不应阻挡相机的碰撞体
        [Header("Misc")]
        public List<GameObject> IgnoredEntities = new List<GameObject>();

        /// <summary>
        /// 负责把环绕相机配置及忽略列表烘焙到实体
        /// </summary>
        public class Baker : Baker<OrbitCameraAuthoring>
        {
            /// <summary>
            /// 创建环绕相机运行时组件和忽略实体缓冲区
            /// </summary>
            /// <param name="authoring">环绕相机 Authoring 配置</param>
            public override void Bake(OrbitCameraAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic | TransformUsageFlags.WorldSpace);

                AddComponent(entity, new OrbitCamera
                {
                    RotationSpeed = authoring.RotationSpeed,
                    MaxVerticalAngle = authoring.MaxVerticalAngle,
                    MinVerticalAngle = authoring.MinVerticalAngle,
                    RotateWithCharacterParent = authoring.RotateWithCharacterParent,

                    MinDistance = authoring.MinDistance,
                    MaxDistance = authoring.MaxDistance,
                    DistanceMovementSpeed = authoring.DistanceMovementSpeed,
                    DistanceMovementSharpness = authoring.DistanceMovementSharpness,

                    ObstructionRadius = authoring.ObstructionRadius,
                    ObstructionInnerSmoothingSharpness = authoring.ObstructionInnerSmoothingSharpness,
                    ObstructionOuterSmoothingSharpness = authoring.ObstructionOuterSmoothingSharpness,
                    PreventFixedUpdateJitter = authoring.PreventFixedUpdateJitter,

                    TargetDistance = authoring.StartDistance,
                    SmoothedTargetDistance = authoring.StartDistance,
                    ObstructedDistance = authoring.StartDistance,

                    PitchAngle = 0f,
                    PlanarForward = -math.forward(),
                });

                AddComponent(entity, new OrbitCameraControl());
                AddComponent<Simulate>(entity);

                DynamicBuffer<OrbitCameraIgnoredEntityBufferElement> ignoredEntitiesBuffer = AddBuffer<OrbitCameraIgnoredEntityBufferElement>(entity);
                for (int i = 0; i < authoring.IgnoredEntities.Count; i++)
                {
                    ignoredEntitiesBuffer.Add(new OrbitCameraIgnoredEntityBufferElement
                    {
                        Entity = GetEntity(authoring.IgnoredEntities[i], TransformUsageFlags.None),
                    });
                }
            }
        }
    }
}
