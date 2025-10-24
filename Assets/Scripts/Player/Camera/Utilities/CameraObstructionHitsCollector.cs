using Unity.Entities;
using Unity.Mathematics;
using Unity.CharacterController;
using Unity.Physics;

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

    public CameraObstructionHitsCollector(Entity followedCharacter, DynamicBuffer<OrbitCameraIgnoredEntityBufferElement> ignoredEntitiesBuffer, float3 cameraDirection)
    {
        NumHits = 0;
        ClosestHit = default;

        _closestHitFraction = float.MaxValue;
        _cameraDirection = cameraDirection;
        _followedCharacter = followedCharacter;
        _ignoredEntitiesBuffer = ignoredEntitiesBuffer;
    }

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

        // Process valid hit
        if (hit.Fraction < _closestHitFraction)
        {
            _closestHitFraction = hit.Fraction;
            ClosestHit = hit;
        }
        NumHits++;

        return true;
    }
}