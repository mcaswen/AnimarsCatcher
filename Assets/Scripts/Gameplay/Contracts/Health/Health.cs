using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay.Contracts
{
    /// <summary>
    /// 保存由服务器维护并同步给客户端的生命值
    /// </summary>
    [GhostComponent]
    public struct Health : IComponentData
    {
        [GhostField]
        public int Current;

        [GhostField]
        public int Maximum;
    }
}
