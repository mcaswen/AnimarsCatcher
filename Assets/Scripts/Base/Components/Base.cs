using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

/// <summary>
/// 标记参与基地规则的实体并同步到所有客户端
/// </summary>
[GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
public struct BaseTag : IComponentData {}

/// <summary>
/// 标记决定对局胜负的大基地
/// </summary>
public struct BigBaseTag : IComponentData {}

/// <summary>
/// 标记不直接决定对局胜负的小基地
/// </summary>
public struct SmallBaseTag : IComponentData {}

/// <summary>
/// 保存基地世界空间 AABB，供 Ani 按碰撞体体积计算感知距离
/// </summary>
public struct BaseWorldAABB : IComponentData
{
    public float3 Center;
    public float3 HalfExtents;
}
