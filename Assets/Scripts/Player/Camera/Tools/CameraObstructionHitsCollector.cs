using Unity.Entities;
using Unity.Mathematics;
using Unity.CharacterController;
using Unity.Physics;

/// <summary>
/// 筛选相机球形投射命中并保留最近的有效遮挡物
/// </summary>
public struct CameraObstructionHitsCollector : ICollector<ColliderCastHit>
{
    public bool EarlyOutOnFirstHit => false;
    public float MaxFraction => 1f;
    public int NumHits { get; private set; }

    public ColliderCastHit ClosestHit;

    private float _closestHitFraction;
    private float3 _cameraDirection;
    private Entity _followedCharacter;
    private DynamicBuffer<OrbitCameraIgnoredEntityBufferElement> _ignoredEntitiesBuffer;

    /// <summary>
    /// 创建一次相机遮挡查询的命中收集器
    /// </summary>
    /// <param name="followedCharacter">当前跟随角色</param>
    /// <param name="ignoredEntitiesBuffer">显式忽略的实体列表</param>
    /// <param name="cameraDirection">相机朝向</param>
    public CameraObstructionHitsCollector(Entity followedCharacter, DynamicBuffer<OrbitCameraIgnoredEntityBufferElement> ignoredEntitiesBuffer, float3 cameraDirection)
    {
        NumHits = 0;
        ClosestHit = default;

        _closestHitFraction = float.MaxValue;
        _cameraDirection = cameraDirection;
        _followedCharacter = followedCharacter;
        _ignoredEntitiesBuffer = ignoredEntitiesBuffer;
    }

    /// <summary>
    /// 过滤自身、忽略实体和不可碰撞表面，并记录最近命中
    /// </summary>
    /// <param name="hit">本次碰撞投射命中</param>
    /// <returns>命中是否参与遮挡计算</returns>
    public bool AddHit(ColliderCastHit hit)
    {
        if (_followedCharacter == hit.Entity)
        {
            return false;
        }

        if (math.dot(hit.SurfaceNormal, _cameraDirection) < 0f || !PhysicsUtilities.IsCollidable(hit.Material))
        {
            return false;
        }

        for (int i = 0; i < _ignoredEntitiesBuffer.Length; i++)
        {
            if (_ignoredEntitiesBuffer[i].Entity == hit.Entity)
            {
                return false;
            }
        }

        // 只保留距离相机目标最近的有效命中
        if (hit.Fraction < _closestHitFraction)
        {
            _closestHitFraction = hit.Fraction;
            ClosestHit = hit;
        }
        NumHits++;

        return true;
    }
}
