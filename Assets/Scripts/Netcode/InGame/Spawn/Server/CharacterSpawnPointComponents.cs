using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Networking
{
    /// <summary>
    /// 标记保存角色出生点集合的实体
    /// </summary>
    public struct CharacterSpawnPointsTag : IComponentData { }

    /// <summary>
    /// 保存单个角色出生点的世界姿态
    /// </summary>
    public struct CharacterSpawnPointElement : IBufferElementData
    {
        public float3 Position;
        public quaternion Rotation;
    }

    /// <summary>
    /// 保存轮询选择出生点时使用的索引状态
    /// </summary>
    public struct CharacterSpawnPointsState : IComponentData
    {
        public int NextIndex;
    }

    /// <summary>
    /// 定义服务器分配角色出生点的策略
    /// </summary>
    public enum CharacterSpawnSelectionMode : byte
    {
        RoundRobin = 0,
        NetworkIdModulo = 1
    }

    /// <summary>
    /// 保存当前出生点集合采用的选择策略
    /// </summary>
    public struct CharacterSpawnSelectionConfig : IComponentData
    {
        public CharacterSpawnSelectionMode Value;
    }
}
