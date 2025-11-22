using Unity.Entities;
using Unity.Mathematics;

// 刷新区域本体（服务器专用，不需要 Ghost）
public struct ResourceSpawnArea : IComponentData
{
    public float3 Center;          // 区域中心
    public float2 HalfExtentsXZ;   // XZ 平面的一半长宽
    public float  SpawnHeightOffset;

    public int MaxFoodCount;       // 区域内最多存在多少 Food
    public int MaxCrystalCount;    // 区域内最多存在多少 Crystal

    public int FoodPerWave;        // 每次刷新最多刷多少 Food
    public int CrystalPerWave;     // 每次刷新最多刷多少 Crystal

    public float RespawnInterval;  // 刷新间隔（秒）
    public float RespawnTimer;     // 计时器

    public float SpawnCheckRadius; // 刷新点周围检查半径
    public int BlockerLayerMask; // 阻挡层

    public int MaxSpawnAttemptsPerResource; // 刷一个资源最多尝试多少次随机点

    public uint RandomSeed;        // 随机数种子
}

// 食物预制体列表
public struct ResourceSpawnFoodPrefab : IBufferElementData
{
    public Entity Prefab;
}

// 水晶预制体列表
public struct ResourceSpawnCrystalPrefab : IBufferElementData
{
    public Entity Prefab;
}
