using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Gameplay.Contracts
{
    /// <summary>
    /// 表示已经通过服务器权限校验的群体移动命令类型
    /// </summary>
    public enum AniSquadCommandMode : byte
    {
        MoveTo,
        Follow,
        Find
    }

    /// <summary>
    /// 指定基础阶段使用的固定阵型布局
    /// </summary>
    public enum AniSquadFormationKind : byte
    {
        Column,
        CompactRectangle
    }

    /// <summary>
    /// 阶段五用于稳定生成前后排槽位的成员职责
    /// </summary>
    public enum AniSquadRole : byte
    {
        Any,
        Picker,
        Blaster
    }

    /// <summary>
    /// 服务器验证后交给 Grid 导航的群体指令
    /// </summary>
    public struct AniSquadCommand : IComponentData
    {
        // 同一玩家的命令序号用于确定跨 World 回放顺序
        public uint Sequence;

        // 只允许服务器写入的连接拥有者编号
        public int OwnerNetworkId;

        public AniSquadCommandMode Mode;
        public AniSquadFormationKind Formation;
        public float3 TargetPosition;
        public Entity TargetEntity;

        // 紧凑矩形的列数上限，阶段五再改为动态列数
        public int FormationColumnCount;

        // Follow 和 Find 在到达该距离后暂时停止，这里不负责安排战斗站位
        public float TargetStoppingDistance;

        // 目标附近没有移动速度时维持的阵型前向
        public float3 DesiredForward;
    }

    /// <summary>
    /// 标记尚未由 Squad 生命周期系统处理的指令 Entity
    /// </summary>
    public struct AniSquadCommandRequest : IComponentData
    {
    }

    /// <summary>
    /// 保存指令中经过权限校验的服务器 Ani 成员及其移动参数
    /// </summary>
    [InternalBufferCapacity(16)]
    public struct AniSquadCommandMember : IBufferElementData
    {
        public Entity Ani;

        // 正式玩法使用 GhostId，Benchmark 使用固定生成索引
        public int StableId;

        public float MaxSpeed;
        public float MaxAcceleration;
        public float AgentRadius;
        public AniSquadRole Role;
    }

    /// <summary>
    /// 服务器专用的 Grid 移动系统总组
    /// </summary>
    [WorldSystemFilter(
        WorldSystemFilterFlags.ServerSimulation |
        WorldSystemFilterFlags.LocalSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class AniGridMovementSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    /// 接收已校验指令和 Benchmark 回放的 Grid 子组
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridMovementSystemGroup), OrderFirst = true)]
    public partial class AniGridCommandIngressSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    /// 运行 Squad 规划、阵型和移动提交的 Grid 子组
    /// </summary>
    [WorldSystemFilter(
        WorldSystemFilterFlags.ServerSimulation |
        WorldSystemFilterFlags.LocalSimulation)]
    [UpdateInGroup(typeof(AniGridMovementSystemGroup))]
    [UpdateAfter(typeof(AniGridCommandIngressSystemGroup))]
    public partial class AniGridRuntimeSystemGroup : ComponentSystemGroup
    {
    }
}
