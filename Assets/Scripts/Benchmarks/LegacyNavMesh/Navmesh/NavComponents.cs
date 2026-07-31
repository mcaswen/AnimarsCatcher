using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 服务端规划出的单个导航路径点
    /// </summary>
    public struct NavWaypoint : IBufferElementData
    {
        public float3 Position;
    }

    /// <summary>
    /// 导航代理配置及服务端路径推进状态
    /// </summary>
    [GhostComponent]
    public struct NavAgent : IComponentData
    {
        [GhostField] public float Speed;
        [GhostField] public float StoppingDistance;
        public int LastHandledNavRequestVersion; // 服务端用于避免重复处理同一寻路请求
        public int CurrentWaypointIndex; // 服务端当前推进到的路径点索引
    }

    /// <summary>
    /// 服务端计算并通过 Ghost 同步的当前转向目标
    /// </summary>
    [GhostComponent]
    public struct NavSteering : IComponentData
    {
        [GhostField] public float3 SteeringTarget;
        [GhostField] public int PathVersion; // 与导航请求版本对齐，供客户端识别新路径
        [GhostField] public byte HasPath;
    }
}
