using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 服务端专用的资源刷新区域状态和随机数种子
    /// </summary>
    public struct ResourceSpawnArea : IComponentData
    {
        public float3 Center;
        // XZ 平面的半尺寸
        public float2 HalfExtentsXZ;
        public float  SpawnHeightOffset;

        // 区域内两类资源各自的实体上限
        public int MaxFoodCount;
        public int MaxCrystalCount;

        // 单个波次允许生成的数量
        public int FoodPerWave;
        public int CrystalPerWave;

        // 两次刷新波次之间的秒数
        public float RespawnInterval;
        public float RespawnTimer;

        // 候选点周围的阻挡检测参数
        public float SpawnCheckRadius;
        public int BlockerLayerMask;

        // 单个资源允许重选候选点的次数
        public int MaxSpawnAttemptsPerResource;

        // 跨帧延续的确定性随机数种子
        public uint RandomSeed;
    }

    /// <summary>
    /// 刷新区域可选择的食物预制体
    /// </summary>
    public struct FoodResourceSpawnPrefabReference : IBufferElementData
    {
        public Entity Prefab;
    }

    /// <summary>
    /// 刷新区域可选择的水晶预制体
    /// </summary>
    public struct CrystalResourceSpawnPrefabReference : IBufferElementData
    {
        public Entity Prefab;
    }
}
