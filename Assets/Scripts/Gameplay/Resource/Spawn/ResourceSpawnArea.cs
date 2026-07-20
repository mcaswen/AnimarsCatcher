using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 服务端专用的资源刷新区域状态和随机数种子
    /// </summary>
    public struct ResourceSpawnArea : IComponentData
    {
        public float3 Center;          // 区域世界坐标中心
        public float2 HalfExtentsXZ;   // XZ 平面的半尺寸
        public float  SpawnHeightOffset;

        public int MaxFoodCount;       // 区域内食物实体上限
        public int MaxCrystalCount;    // 区域内水晶实体上限

        public int FoodPerWave;        // 单波最多生成的食物数量
        public int CrystalPerWave;     // 单波最多生成的水晶数量

        public float RespawnInterval;  // 两次刷新波次之间的秒数
        public float RespawnTimer;     // 当前波次累计时间

        public float SpawnCheckRadius; // 候选点周围的阻挡检测半径
        public int BlockerLayerMask; // 参与阻挡检测的层掩码

        public int MaxSpawnAttemptsPerResource; // 单个资源允许重选候选点的次数

        public uint RandomSeed;        // 跨帧延续的确定性随机数种子
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
