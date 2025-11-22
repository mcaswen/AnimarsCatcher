using Unity.Mathematics;

public static class DistanceUtil
{
    // 点到 AABB 的距离平方，如果点在盒子内部，则返回 0
    public static float DistanceSqToAABB(float3 point, in BaseWorldAABB aabb)
    {
        float3 min = aabb.Center - aabb.HalfExtents;
        float3 max = aabb.Center + aabb.HalfExtents;

        float dx = math.max(math.max(min.x - point.x, 0f), point.x - max.x);
        float dy = math.max(math.max(min.y - point.y, 0f), point.y - max.y);
        float dz = math.max(math.max(min.z - point.z, 0f), point.z - max.z);

        return dx * dx + dy * dy + dz * dz;
    }
}
