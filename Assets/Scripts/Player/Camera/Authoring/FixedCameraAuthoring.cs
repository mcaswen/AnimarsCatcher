using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;
using Unity.Transforms;

[DisallowMultipleComponent]
public class FixedCameraAuthoring : MonoBehaviour
{

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

    class Baker : Baker<FixedCameraAuthoring>
    {
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