using Unity.Mathematics;
using System.Runtime.CompilerServices;
using Unity.CharacterController;

/// <summary>提供角色旋转角度提取所需的数学工具</summary>
public static class CustomMathUtilities
{
    /// <summary>从四元数提取最短旋转角度</summary>
    /// <param name="q">待解析四元数</param>
    /// <returns>角度制旋转量</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AngleDegFromQuaternion(quaternion q)
    {
        // 使用 axis-angle 公式并取 w 的绝对值以获得最短旋转角
        float4 v = q.value;
        float ang = 2f * math.atan2(math.length(new float3(v.x, v.y, v.z)), math.abs(v.w));
        return math.degrees(ang);
    }

    /// <summary>计算指定上方向平面内的偏航角</summary>
    /// <param name="rot">待解析旋转</param>
    /// <param name="up">参考上方向</param>
    /// <returns>角度制偏航量</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float YawDegFromRotation(quaternion rot, float3 up)
    {
        // 使用同一上方向投影当前前向量，避免倾斜地面污染偏航角
        float3 f = math.mul(rot, new float3(0, 0, 1));
        f = MathUtilities.ProjectOnPlane(f, up);
        if (math.lengthsq(f) < 1e-8f) return 0f;
        f = math.normalize(f);
        return math.degrees(math.atan2(f.x, f.z));
    }

    /// <summary>计算世界方向在水平面内的偏航角</summary>
    /// <param name="dirWS">世界空间方向</param>
    /// <returns>角度制偏航量</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float YawDegFromDir(float3 dirWS)
    {
        dirWS.y = 0;
        if (math.lengthsq(dirWS) < 1e-8f) return 0f;
        dirWS = math.normalize(dirWS);
        return math.degrees(math.atan2(dirWS.x, dirWS.z));
    }
}
