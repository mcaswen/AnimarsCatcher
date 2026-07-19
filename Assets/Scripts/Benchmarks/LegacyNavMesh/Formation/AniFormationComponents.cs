using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 保存 Ani 当前所属队长及其稳定阵型槽位
    /// </summary>
    [GhostComponent]
    public struct AniFormationMember : IComponentData
    {
        [GhostField]
        public Entity leader;

        [GhostField]
        public int slotIndex;
    }

    /// <summary>
    /// 请求服务器把 Ani 加入或迁移到指定队长的阵型
    /// </summary>
    [GhostComponent]
    public struct AniFormationJoinRequest : IComponentData
    {
        [GhostField]
        public Entity leader;
    }

    /// <summary>
    /// 请求服务器释放 Ani 在指定队长阵型中的成员关系
    /// </summary>
    [GhostComponent]
    public struct AniFormationLeaveRequest : IComponentData
    {
        [GhostField]
        public Entity leader;
    }
}
