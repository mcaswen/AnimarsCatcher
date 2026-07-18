using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay.Contracts
{
/// <summary>
/// 表示对局中的两个阵营以及不参与敌我判断的中立阵营
/// </summary>
public enum CampType : byte
{
    Alpha = 0,
    Beta = 1,
    Neutral = 2
}

/// <summary>
/// 保存实体阵营并通过 Ghost 同步给客户端
/// </summary>
[GhostComponent]
public struct Camp : IComponentData
{
    [GhostField] public CampType Value;
}

/// <summary>
/// 保存玩家连接的服务器权威阵营归属
/// </summary>
public struct PlayerCamp : IComponentData
{
    public CampType Value;
}

/// <summary>
/// 保存当前客户端本地玩家的阵营快照
/// </summary>
public struct LocalPlayerCamp : IComponentData
{
    public CampType Value;
}
}
