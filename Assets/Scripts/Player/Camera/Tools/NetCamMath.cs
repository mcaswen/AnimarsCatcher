using Unity.Mathematics;
using Unity.CharacterController;

public static class NetCamMath
{
    public static void BuildCameraBasis(
        float3 up,
        in OrbitCamera camera,            // 上一帧相机状态
        in float2 lookDeltaDegrees,    // 本帧输入增量
        out quaternion cameraRotation,
        out float3 forwardOnUpPlane,
        out float3 right,
        out float newPitch)
    {
        float3 planarFwd = camera.PlanarForward;

        // yaw: 绕 up 旋转 Look.x * RotationSpeed
        float yawDeg = lookDeltaDegrees.x * camera.RotationSpeed;
        quaternion yawRot = quaternion.Euler(up * math.radians(yawDeg));
        planarFwd = math.rotate(yawRot, planarFwd);

        // pitch: 叠加并 clamp
        newPitch = math.clamp(camera.PitchAngle + (-lookDeltaDegrees.y * camera.RotationSpeed),
                              camera.MinVAngle, camera.MaxVAngle);

        cameraRotation = OrbitCameraUtilities.CalculateCameraRotation(up, planarFwd, newPitch);

        forwardOnUpPlane = math.normalizesafe(
            MathUtilities.ProjectOnPlane(MathUtilities.GetForwardFromRotation(cameraRotation), up));
        right = MathUtilities.GetRightFromRotation(cameraRotation);
    }
}
