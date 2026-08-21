namespace AnimarsCatcher.Player
{
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Mathematics;
    using Unity.NetCode;
    using Unity.Transforms;

    /// <summary>
    /// 在客户端表现阶段把主相机同步到当前主相机 Entity
    /// </summary>
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class MainCameraSystem : SystemBase
    {
        private EntityQuery _cameraEntityQuery;

        protected override void OnCreate()
        {
            _cameraEntityQuery = SystemAPI.QueryBuilder()
                .WithAll<MainEntityCameraTag>()
                .Build();

            RequireForUpdate(_cameraEntityQuery);
        }

        protected override void OnUpdate()
        {
            // 过场播放时由过场系统独占主相机，避免多个系统同时写入 Transform
            if (ClientCinematicState.IsRunning)
                return;

            if (MainGameObjectCamera.Instance != null)
            {
                // 查询结果使用临时数组，确保遍历期间 Entity 集合保持稳定
                using NativeArray<Entity> entities = _cameraEntityQuery.ToEntityArray(Allocator.Temp);

                foreach (Entity mainEntityCamera in entities)
                {
                    LocalToWorld targetLocalToWorld = SystemAPI.GetComponent<LocalToWorld>(mainEntityCamera);

                    MainGameObjectCamera.Instance.transform.SetPositionAndRotation(targetLocalToWorld.Position, targetLocalToWorld.Rotation);
                }
            }
        }
    }
}
