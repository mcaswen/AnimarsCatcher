using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 配置可由服务端规划路径的导航代理
/// </summary>
[DisallowMultipleComponent]
public class NavAgentAuthoring : MonoBehaviour
{
    [Header("NavAgent Config")]
    public float Speed = 3.5f;
    public float StoppingDistance = 0.5f;
    public Transform[] InitialWaypoints;
}

/// <summary>
/// 将导航代理配置和初始路径点烘焙为 ECS 数据
/// </summary>
public class NavAgentBaker : Baker<NavAgentAuthoring>
{
    /// <summary>
    /// 创建导航状态 转向状态和路径点缓冲区
    /// </summary>
    /// <param name="authoring">场景中的导航代理配置</param>
    public override void Bake(NavAgentAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.Dynamic);

        AddComponent(entity, new NavAgent
        {
            Speed = authoring.Speed,
            StoppingDistance = authoring.StoppingDistance,
            LastHandledNavRequestVersion = -1, 
            CurrentWaypointIndex = -1         
        });

        AddComponent(entity, new NavSteering
        {
            SteeringTarget = float3.zero,
            PathVersion = 0,
            HasPath = 0
        });

        // 即使没有初始路径点也创建缓冲区 便于运行时直接写入规划结果
        var buf = AddBuffer<NavWaypoint>(entity);

        if (authoring.InitialWaypoints != null && authoring.InitialWaypoints.Length > 0)
        {
            for (int i = 0; i < authoring.InitialWaypoints.Length; i++)
            {
                var t = authoring.InitialWaypoints[i];
                if (t != null)
                {
                    buf.Add(new NavWaypoint { Position = t.position });
                }
            }
        }
    }
}
