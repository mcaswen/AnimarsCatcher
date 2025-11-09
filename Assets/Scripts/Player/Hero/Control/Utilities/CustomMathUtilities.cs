using Unity.Mathematics;
using System.Runtime.CompilerServices;
using Unity.CharacterController;

public static class CustomMathUtilities
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AngleDegFromQuaternion(quaternion q)
    {
        // axis-angle：angle = 2 * atan2(|v|, w)
        float4 v = q.value;
        float ang = 2f * math.atan2(math.length(new float3(v.x, v.y, v.z)), math.abs(v.w));
        return math.degrees(ang);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float YawDegFromRotation(quaternion rot, float3 up)
    {
        // 用同一个 up 投影当前 forward，算水平面的 yaw
        float3 f = math.mul(rot, new float3(0, 0, 1));
        f = MathUtilities.ProjectOnPlane(f, up);
        if (math.lengthsq(f) < 1e-8f) return 0f;
        f = math.normalize(f);
        return math.degrees(math.atan2(f.x, f.z));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float YawDegFromDir(float3 dirWS)
    {
        dirWS.y = 0;
        if (math.lengthsq(dirWS) < 1e-8f) return 0f;
        dirWS = math.normalize(dirWS);
        return math.degrees(math.atan2(dirWS.x, dirWS.z));
    }
}