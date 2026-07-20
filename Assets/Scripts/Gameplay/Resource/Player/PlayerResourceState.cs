using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{

    /// <summary>
    /// 服务端按玩家维护并通过 Ghost 同步的资源统计
    /// </summary>
    [GhostComponent]
    public struct PlayerResourceState : IComponentData
    {
        // 当前玩家拥有的两类 Ani 总数
        [GhostField] public int TotalPickerAniCount;
        [GhostField] public int TotalBlasterAniCount;

        // 当前加入队伍的两类 Ani 数量
        [GhostField] public int InTeamPickerAniCount;
        [GhostField] public int InTeamBlasterAniCount;

        // 当前被玩家选中的两类 Ani 数量
        [GhostField] public int SelectedPickerAniCount;
        [GhostField] public int SelectedBlasterAniCount;

        // 可用于玩法消耗的资源总量
        [GhostField] public int FoodAmount;
        [GhostField] public int CrystalAmount;
    }

    /// <summary>
    /// 标识玩家资源 Ghost 实体
    /// </summary>
    public struct PlayerResourceTag : IComponentData {}
}
