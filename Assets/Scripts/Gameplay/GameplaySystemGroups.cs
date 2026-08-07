using Unity.Entities;
using AnimarsCatcher.Gameplay.Contracts;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在导航和物理移动完成后运行需要读取最终位置的玩法系统
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AniGridMovementSystemGroup))]
    public partial class GameplayPostMovementSystemGroup : ComponentSystemGroup
    {
    }
}
