using Unity.Mathematics;
using Unity.CharacterController;

/// <summary>
/// 提供网络相机输入到相机基向量的确定性计算
/// </summary>
public static class NetworkCameraMath
{
    /// <summary>
    /// 根据上一帧状态和本帧输入构建相机旋转基
    /// </summary>
    /// <param name="up">目标上方向</param>
    /// <param name="camera">上一帧相机状态</param>
    /// <param name="lookDeltaDegrees">本帧视角输入增量</param>
    /// <param name="cameraRotation">输出相机旋转</param>
    /// <param name="forwardOnUpPlane">输出投影到目标平面的前方向</param>
    /// <param name="right">输出相机右方向</param>
    /// <param name="newPitchAngle">输出更新后的俯仰角</param>
    public static void BuildCameraBasis(
        float3 up,
        in OrbitCamera camera,            // 上一帧相机状态
        in float2 lookDeltaDegrees,    // 本帧输入增量
        out quaternion cameraRotation,
        out float3 forwardOnUpPlane,
        out float3 right,
        out float newPitchAngle)
    {
        float3 planarForward = camera.PlanarForward;

        // 偏航输入只绕目标上方向旋转
        float yawDegrees = lookDeltaDegrees.x * camera.RotationSpeed;
        quaternion yawRotation = quaternion.Euler(up * math.radians(yawDegrees));
        planarForward = math.rotate(yawRotation, planarForward);

        // 俯仰输入需要累计并限制在配置角度内
        newPitchAngle = math.clamp(camera.PitchAngle + (-lookDeltaDegrees.y * camera.RotationSpeed),
                                   camera.MinVerticalAngle, camera.MaxVerticalAngle);

        cameraRotation = OrbitCameraUtilities.CalculateCameraRotation(up, planarForward, newPitchAngle);

        forwardOnUpPlane = math.normalizesafe(
            MathUtilities.ProjectOnPlane(MathUtilities.GetForwardFromRotation(cameraRotation), up));
        right = MathUtilities.GetRightFromRotation(cameraRotation);
    }
}
