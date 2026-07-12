using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>配置固定第三人称相机的视角和网络吸附参数</summary>
[DisallowMultipleComponent]
public class FixedCameraAuthoring : MonoBehaviour
{

    // 固定视角参数决定相机相对角色的稳定构图
    [Header("Fixed Config")]
    
    [Tooltip("Distance")]
    public float Distance = 6f;

    [Tooltip("Vertical angle")]
    [Range(-89, 89)] public float PitchDeg = 20f;

    [Tooltip("Horizontal angle")]
    public float YawDeg = 45f;

    [Tooltip("Target Height")]
    public float Height = 1.5f;

    [Tooltip("Friction Damping")]
    public float Damping = 0.12f;

    [Tooltip("Look Up Bias")]
    public float LookUpBias = 0.8f;

    // 网络偏差超过阈值时跳过阻尼直接吸附
    [Header("Network Snap Settings")]
    public float SnapDistance = 0.5f;
    public float SnapAngleDeg = 8f;

    /// <summary>负责把固定相机配置烘焙到实体</summary>
    class Baker : Baker<FixedCameraAuthoring>
    {
        /// <summary>创建固定相机运行时组件</summary>
        /// <param name="authoring">固定相机 Authoring 配置</param>
        public override void Bake(FixedCameraAuthoring authoring)
        {

            var cameraEntity = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent(cameraEntity, new FixedCamera
            {
                Distance = authoring.Distance,
                PitchDeg = authoring.PitchDeg,
                YawDeg = authoring.YawDeg,
                Height = authoring.Height,
                Damping = math.max(0.0001f, authoring.Damping),
                LookUpBias = authoring.LookUpBias
            });

            AddComponent<FixedCameraSmoothState>(cameraEntity);
            AddComponent<FixedCameraControl>(cameraEntity);
        }
    }
}
