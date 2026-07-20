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
        [FormerlySerializedAs("RotationSpeed")]
        [Header("Rotation")]
        [SerializeField] private float _rotationSpeed = 2f;
        [FormerlySerializedAs("MaxVerticalAngle")]
        [FormerlySerializedAs("MaxVAngle")]
        [SerializeField] private float _maximumVerticalAngle = 89f;
        [FormerlySerializedAs("MinVerticalAngle")]
        [FormerlySerializedAs("MinVAngle")]
        [SerializeField] private float _minimumVerticalAngle = -89f;
        [FormerlySerializedAs("RotateWithCharacterParent")]
        [SerializeField] private bool _rotateWithCharacterParent = true;

        // 距离平滑与输入速度分开配置，避免缩放手感依赖帧率
        [FormerlySerializedAs("StartDistance")]
        [Header("Distance")]
        [SerializeField] private float _startDistance = 5f;
        [FormerlySerializedAs("MinDistance")]
        [SerializeField] private float _minimumDistance = 0f;
        [FormerlySerializedAs("MaxDistance")]
        [SerializeField] private float _maximumDistance = 10f;
        [FormerlySerializedAs("DistanceMovementSpeed")]
        [SerializeField] private float _distanceMovementSpeed = 1f;
        [FormerlySerializedAs("DistanceMovementSharpness")]
        [SerializeField] private float _distanceMovementSharpness = 20f;

        // 遮挡收缩和恢复使用不同平滑强度，减少穿模和相机弹跳
        [FormerlySerializedAs("ObstructionRadius")]
        [Header("Obstructions")]
        [SerializeField] private float _obstructionRadius = 0.1f;
        [FormerlySerializedAs("ObstructionInnerSmoothingSharpness")]
        [SerializeField] private float _obstructionInnerSmoothingSharpness = float.MaxValue;
        [FormerlySerializedAs("ObstructionOuterSmoothingSharpness")]
        [SerializeField] private float _obstructionOuterSmoothingSharpness = 5f;
        [FormerlySerializedAs("PreventFixedUpdateJitter")]
        [SerializeField] private bool _preventFixedUpdateJitter = true;

        // 忽略列表用于排除角色附件等不应阻挡相机的碰撞体
        [FormerlySerializedAs("IgnoredEntities")]
        [Header("Misc")]
        [SerializeField] private List<GameObject> _ignoredEntities = new List<GameObject>();

        private sealed class Baker : Baker<OrbitCameraAuthoring>
        {
            public override void Bake(OrbitCameraAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic | TransformUsageFlags.WorldSpace);

                AddComponent(entity, new OrbitCamera
                {
                    RotationSpeed = authoring._rotationSpeed,
                    MaxVerticalAngle = authoring._maximumVerticalAngle,
                    MinVerticalAngle = authoring._minimumVerticalAngle,
                    RotateWithCharacterParent = authoring._rotateWithCharacterParent,

                    MinDistance = authoring._minimumDistance,
                    MaxDistance = authoring._maximumDistance,
                    DistanceMovementSpeed = authoring._distanceMovementSpeed,
                    DistanceMovementSharpness = authoring._distanceMovementSharpness,

                    ObstructionRadius = authoring._obstructionRadius,
                    ObstructionInnerSmoothingSharpness = authoring._obstructionInnerSmoothingSharpness,
                    ObstructionOuterSmoothingSharpness = authoring._obstructionOuterSmoothingSharpness,
                    PreventFixedUpdateJitter = authoring._preventFixedUpdateJitter,

                    TargetDistance = authoring._startDistance,
                    SmoothedTargetDistance = authoring._startDistance,
                    ObstructedDistance = authoring._startDistance,

                    PitchAngle = 0f,
                    PlanarForward = -math.forward(),
                });

                AddComponent(entity, new OrbitCameraControl());
                AddComponent<Simulate>(entity);

                DynamicBuffer<OrbitCameraIgnoredEntityBufferElement> ignoredEntitiesBuffer = AddBuffer<OrbitCameraIgnoredEntityBufferElement>(entity);
                for (int i = 0; i < authoring._ignoredEntities.Count; i++)
                {
                    ignoredEntitiesBuffer.Add(new OrbitCameraIgnoredEntityBufferElement
                    {
                        Entity = GetEntity(authoring._ignoredEntities[i], TransformUsageFlags.None),
                    });
                }
            }
        }
    }
}
