using Unity.Entities;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在导航和物理移动完成后运行需要读取最终位置的玩法系统
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class GameplayPostMovementSystemGroup : ComponentSystemGroup
    {
    }
}
