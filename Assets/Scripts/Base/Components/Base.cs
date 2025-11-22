using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

[GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
public struct BaseTag : IComponentData {}

public struct BigBaseTag   : IComponentData {}   // 大基地
public struct SmallBaseTag : IComponentData {}   // 小基地

// 基地的世界空间 AABB，用于 Ani 感知距离（“碰撞体体积”）
public struct BaseWorldAABB : IComponentData
{
    public float3 Center;
    public float3 HalfExtents;
}