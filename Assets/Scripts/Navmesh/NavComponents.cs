using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

// 当前路径点，用于权威服务器的计算
public struct NavWaypoint : IBufferElementData
{
    public float3 Position;
}

// 导航代理配置
[GhostComponent]
public struct NavAgent : IComponentData
{
    [GhostField] public float Speed;
    [GhostField] public float StoppingDistance;
    public int LastHandledNavRequestVersion; // 仅用于服务端，避免重复寻路
    public int CurrentWaypointIndex; // 仅用于服务端推进
}

// 服务端算出的“当前转向目标”
[GhostComponent]
public struct NavSteering : IComponentData
{
    [GhostField] public float3 SteeringTarget;
    [GhostField] public int PathVersion; // 与 K_NavRequestVersion 对齐，便于客户端判断是否新路径
    [GhostField] public byte HasPath; 
}