using AnimarsCatcher.Gameplay.Contracts;
using Unity.Mathematics;

/// <summary>
/// 提供不分配内存的空间距离计算方法
/// </summary>
public static class DistanceUtility
{
    /// <summary>
    /// 计算点到轴对齐包围盒的距离平方，点位于盒内时返回零
    /// </summary>
    /// <param name="point">待测世界坐标</param>
    /// <param name="aabb">基地的世界空间包围盒</param>
    /// <returns>点到包围盒最近位置的距离平方</returns>
    public static float DistanceSquaredToAABB(float3 point, in BaseWorldAABB aabb)
    {
        float3 min = aabb.Center - aabb.HalfExtents;
        float3 max = aabb.Center + aabb.HalfExtents;

        float dx = math.max(math.max(min.x - point.x, 0f), point.x - max.x);
        float dy = math.max(math.max(min.y - point.y, 0f), point.y - max.y);
        float dz = math.max(math.max(min.z - point.z, 0f), point.z - max.z);

        return dx * dx + dy * dy + dz * dz;
    }
}
