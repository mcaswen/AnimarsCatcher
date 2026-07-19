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
        [Header("Fixed Config")]

        public float Distance = 6f;

        [FormerlySerializedAs("PitchDeg")]
        [Range(-89, 89)] public float PitchDegrees = 20f;

        [FormerlySerializedAs("YawDeg")]
        public float YawDegrees = 45f;

        [Tooltip("相机相对目标的垂直偏移，单位米")]
        public float Height = 1.5f;

        public float Damping = 0.12f;

        [Tooltip("观察点相对目标的垂直偏移，单位米")]
        public float LookUpBias = 0.8f;

        // 网络偏差超过阈值时跳过阻尼直接吸附
        [Header("Network Snap Settings")]
        public float SnapDistance = 0.5f;
        [FormerlySerializedAs("SnapAngleDeg")]
        public float SnapAngleDegrees = 8f;

        class Baker : Baker<FixedCameraAuthoring>
        {
            public override void Bake(FixedCameraAuthoring authoring)
            {

                var cameraEntity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponent(cameraEntity, new FixedCamera
                {
                    Distance = authoring.Distance,
                    PitchDeg = authoring.PitchDegrees,
                    YawDeg = authoring.YawDegrees,
                    Height = authoring.Height,
                    Damping = math.max(0.0001f, authoring.Damping),
                    LookUpBias = authoring.LookUpBias
                });

                AddComponent<FixedCameraSmoothState>(cameraEntity);
                AddComponent<FixedCameraControl>(cameraEntity);
            }
        }
    }
}
