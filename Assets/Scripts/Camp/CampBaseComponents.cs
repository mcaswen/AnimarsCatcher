using Unity.Entities;
using Unity.NetCode;

public enum CampType : byte
{
    Alpha = 0,
    Beta = 1,
    Neutral = 2
}

// 阵营组件，附加在所有要参与规则判断的实体上
[GhostComponent]
public struct Camp : IComponentData
{
    [GhostField] public CampType Value;
}

// 仅用于“玩家/连接”的阵营归属，供服务器查询
public struct PlayerCamp : IComponentData
{
    public CampType Value;
}