using Unity.Mathematics;
using System.Runtime.CompilerServices;
using Unity.CharacterController;

/// <summary>
/// 提供角色旋转角度提取所需的数学工具
/// </summary>
public static class CustomMathUtilities
{
    /// <summary>
    /// 从四元数提取最短旋转角度
    /// </summary>
    /// <param name="rotation">待解析四元数</param>
    /// <returns>角度制旋转量</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AngleDegreesFromQuaternion(quaternion rotation)
    {
        // 使用 axis-angle 公式并取 w 的绝对值以获得最短旋转角
        float4 quaternionValue = rotation.value;
        float angleRadians = 2f * math.atan2(
            math.length(new float3(quaternionValue.x, quaternionValue.y, quaternionValue.z)),
            math.abs(quaternionValue.w));
        return math.degrees(angleRadians);
    }

    /// <summary>
    /// 计算指定上方向平面内的偏航角
    /// </summary>
    /// <param name="rotation">待解析旋转</param>
    /// <param name="up">参考上方向</param>
    /// <returns>角度制偏航量</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float YawDegreesFromRotation(quaternion rotation, float3 up)
    {
        // 使用同一上方向投影当前前向量，避免倾斜地面污染偏航角
        float3 forward = math.mul(rotation, new float3(0, 0, 1));
        forward = MathUtilities.ProjectOnPlane(forward, up);
        if (math.lengthsq(forward) < 1e-8f) return 0f;
        forward = math.normalize(forward);
        return math.degrees(math.atan2(forward.x, forward.z));
    }

    /// <summary>
    /// 计算世界方向在水平面内的偏航角
    /// </summary>
    /// <param name="worldSpaceDirection">世界空间方向</param>
    /// <returns>角度制偏航量</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float YawDegreesFromDirection(float3 worldSpaceDirection)
    {
        worldSpaceDirection.y = 0;
        if (math.lengthsq(worldSpaceDirection) < 1e-8f) return 0f;
        worldSpaceDirection = math.normalize(worldSpaceDirection);
        return math.degrees(math.atan2(worldSpaceDirection.x, worldSpaceDirection.z));
    }
}
