using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using Unity.VisualScripting;
using AnimarsCatcher.Mono.Global;

/// <summary>
/// 在客户端表现阶段把主相机同步到当前主相机实体
/// </summary>
[UpdateInGroup(typeof(PresentationSystemGroup))]
public partial class MainCameraSystem : SystemBase
{
    private EntityQuery _cameraEntityQuery;

    /// <summary>
    /// 创建主相机实体查询并将其作为系统运行条件
    /// </summary>
    protected override void OnCreate()
    {
        _cameraEntityQuery = SystemAPI.QueryBuilder()
            .WithAll<MainEntityCamera>()
            .Build();

        RequireForUpdate(_cameraEntityQuery);
    }

    /// <summary>
    /// 在表现帧末将实体世界姿态应用到 Unity Camera
    /// </summary>
    protected override void OnUpdate()
    {
        // 过场播放时由过场系统独占主相机，避免多个系统同时写入 Transform
        if (ClientCinematicState.IsRunning)
            return;

        if (MainGameObjectCamera.Instance != null)
        {
            // 查询结果使用临时数组，确保遍历期间实体集合保持稳定
            using NativeArray<Entity> entities = _cameraEntityQuery.ToEntityArray(Allocator.Temp);

            foreach (Entity mainEntityCamera in entities)
            {
                LocalToWorld targetLocalToWorld = SystemAPI.GetComponent<LocalToWorld>(mainEntityCamera);

                MainGameObjectCamera.Instance.transform.SetPositionAndRotation(targetLocalToWorld.Position, targetLocalToWorld.Rotation);
            }
        }
    }
}
