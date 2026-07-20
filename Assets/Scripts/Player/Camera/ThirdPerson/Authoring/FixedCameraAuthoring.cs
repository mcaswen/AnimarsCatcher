namespace AnimarsCatcher.Player
{
    using Unity.Entities;
    using UnityEngine;
    using UnityEngine.Serialization;
    using Unity.Mathematics;
    using Unity.Transforms;

    /// <summary>
    /// 配置固定第三人称相机的视角和网络吸附参数
    /// </summary>
    [DisallowMultipleComponent]
    public class FixedCameraAuthoring : MonoBehaviour
    {
        // 固定视角参数决定相机相对角色的稳定构图
        [FormerlySerializedAs("Distance")]
        [Header("Fixed Config")]
        [SerializeField] private float _distance = 6f;

        [FormerlySerializedAs("PitchDeg")]
        [FormerlySerializedAs("PitchDegrees")]
        [Range(-89, 89)]
        [SerializeField] private float _pitchDegrees = 20f;

        [FormerlySerializedAs("YawDeg")]
        [FormerlySerializedAs("YawDegrees")]
        [SerializeField] private float _yawDegrees = 45f;

        [FormerlySerializedAs("Height")]
        [Tooltip("相机相对目标的垂直偏移，单位米")]
        [SerializeField] private float _height = 1.5f;

        [FormerlySerializedAs("Damping")]
        [SerializeField] private float _damping = 0.12f;

        [FormerlySerializedAs("LookUpBias")]
        [Tooltip("观察点相对目标的垂直偏移，单位米")]
        [SerializeField] private float _lookUpBias = 0.8f;

        // 网络偏差超过阈值时跳过阻尼直接吸附
        [FormerlySerializedAs("SnapDistance")]
        [Header("Network Snap Settings")]
        [SerializeField] private float _snapDistance = 0.5f;
        [FormerlySerializedAs("SnapAngleDegrees")]
        [FormerlySerializedAs("SnapAngleDeg")]
        [SerializeField] private float _snapAngleDegrees = 8f;

        private sealed class Baker : Baker<FixedCameraAuthoring>
        {
            public override void Bake(FixedCameraAuthoring authoring)
            {
                var cameraEntity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponent(cameraEntity, new FixedCamera
                {
                    Distance = authoring._distance,
                    PitchDeg = authoring._pitchDegrees,
                    YawDeg = authoring._yawDegrees,
                    Height = authoring._height,
                    Damping = math.max(0.0001f, authoring._damping),
                    LookUpBias = authoring._lookUpBias,
                    SnapDistance = math.max(0f, authoring._snapDistance),
                    SnapAngleDeg = math.max(0f, authoring._snapAngleDegrees)
                });

                AddComponent<FixedCameraSmoothState>(cameraEntity);
                AddComponent<FixedCameraControl>(cameraEntity);
            }
        }
    }
}
