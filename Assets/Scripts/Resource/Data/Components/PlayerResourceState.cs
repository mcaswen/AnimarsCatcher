using Unity.Entities;
using Unity.NetCode;


[GhostComponent] 
public struct PlayerResourceState : IComponentData
{
    // Ani 总数
    [GhostField] public int TotalPickerAniCount;
    [GhostField] public int TotalBlasterAniCount;

    // 当前在队伍中的 Ani 数量
    [GhostField] public int InTeamPickerAniCount;
    [GhostField] public int InTeamBlasterAniCount;

    // 当前选中的 Ani 数量
    [GhostField] public int SelectedPickerAniCount;
    [GhostField] public int SelectedBlasterAniCount;

    // 资源数量
    [GhostField] public int FoodSum;
    [GhostField] public int CrystalSum;
}

public struct PlayerResourceTag : IComponentData {}

