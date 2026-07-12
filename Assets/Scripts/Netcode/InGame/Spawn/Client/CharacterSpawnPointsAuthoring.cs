using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>标记保存角色出生点集合的实体</summary>
public struct CharacterSpawnPointsTag : IComponentData {}
/// <summary>保存单个角色出生点的世界姿态</summary>
public struct CharacterSpawnPointElement : IBufferElementData
{
    /// <summary>出生点世界位置</summary>
    public float3 Position;
    /// <summary>出生点世界旋转</summary>
    public quaternion Rotation;
}
/// <summary>保存轮询选择出生点时使用的索引状态</summary>
public struct CharacterSpawnPointsState : IComponentData
{
    /// <summary>轮询模式下一次尝试的索引</summary>
    public int NextIndex; 
}

/// <summary>定义服务器分配角色出生点的策略</summary>
public enum SpawnSelectMode : byte
{
    RoundRobin = 0,
    NetworkIdModulo = 1
}

/// <summary>保存当前出生点集合采用的选择策略</summary>
public struct CharacterSpawnSelectMode : IComponentData 
{
    /// <summary>当前出生点集合的选择策略</summary>
    public SpawnSelectMode Value;     
}

/// <summary>配置某个阵营的出生点集合和选择策略</summary>
public class CharacterSpawnPointsAuthoring : MonoBehaviour
{
    [Tooltip("Select Mode: RoundRobin or NetworkIdModulo")]
    public SpawnSelectMode selectMode = SpawnSelectMode.RoundRobin;
    public CampType campType = CampType.Alpha;
}
