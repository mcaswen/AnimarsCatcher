using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 由服务端维护并同步给客户端的全局比赛状态
    /// </summary>
    [GhostComponent]
    public struct GlobalGameResourceState : IComponentData
    {
        [GhostField] public int MatchTimeSeconds;
    }

    /// <summary>
    /// 标识唯一的全局比赛资源实体
    /// </summary>
    public struct GlobalGameResourceTag : IComponentData { }
}
