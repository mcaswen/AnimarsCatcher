using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay.Contracts
{
    /// <summary>
    /// 保存服务器权威生命值并同步给客户端
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
