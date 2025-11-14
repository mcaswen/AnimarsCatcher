using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

[UpdateInGroup(typeof(PresentationSystemGroup))]
public partial class MainCameraSystem : SystemBase
{
    private EntityQuery _cameraEntityQuery;

    protected override void OnCreate()
    {
        _cameraEntityQuery = SystemAPI.QueryBuilder()
            .WithAll<MainEntityCamera, GhostOwner>()
            .Build();

        RequireForUpdate(_cameraEntityQuery);
    }

    protected override void OnUpdate()
    {
        if (MainGameObjectCamera.Instance != null)
        {
            using NativeArray<Entity> entities = _cameraEntityQuery.ToEntityArray(Allocator.Temp);

            foreach (Entity mainEntityCamera in entities)
            {
                LocalToWorld targetLocalToWorld = SystemAPI.GetComponent<LocalToWorld>(mainEntityCamera);

                MainGameObjectCamera.Instance.transform.SetPositionAndRotation(targetLocalToWorld.Position, targetLocalToWorld.Rotation);
            }
        }
    }
}