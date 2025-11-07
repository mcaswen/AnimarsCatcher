using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;


[DisallowMultipleComponent]
public class AvatarViewFollower : MonoBehaviour
{
    public Entity TargetEntity;
    public EntityManager BoundEntityManager;

    [Tooltip("Animator 移速 参数名")]
    public string SpeedParameterName = "Speed";

    [Tooltip("速度指数平滑强度")]
    public float SpeedSmoothingStrength = 12f;
    
    [Tooltip("瞬移吸附距离阈值")]
    public float TeleportSnapDistance = 2.0f;

    [Tooltip("位移死区")]
    public float SpeedDeadbandMeters = 0.0f;

    [Tooltip("LocalToWorld 读取")]
    public bool PreferLocalToWorld = true;

    private Animator _animator;
    private bool _initialized;
    private Vector3 _lastRenderPosition;

    private Vector3 _appliedPos;
    private Quaternion _appliedRot;


    // 由生成系统在实例化后调用
    public void Bind(Entity entity, EntityManager entityManager)
    {
        TargetEntity = entity;
        BoundEntityManager = entityManager;
        _initialized = false;
    }

    private void Awake()
    {
        _animator = GetComponentInChildren<Animator>();
        if (_animator != null)
            _animator.applyRootMotion = false;

        _lastRenderPosition = transform.position;
    }

    private void LateUpdate()
    {
        if (BoundEntityManager == default || !BoundEntityManager.Exists(TargetEntity))
        {
            Destroy(gameObject);
            return;
        }
        if (!BoundEntityManager.HasComponent<AvatarViewSpawnedTag>(TargetEntity))
        {
            Destroy(gameObject);
            return;
        }

        float3 targetEntityPosition;
        quaternion targetEntityRotation;
        float targetEntityUniformScale = 1f;

        targetEntityPosition = BoundEntityManager.GetComponentData<LocalTransform>(TargetEntity).Position;
        targetEntityRotation = BoundEntityManager.GetComponentData<LocalTransform>(TargetEntity).Rotation;
        targetEntityUniformScale = BoundEntityManager.GetComponentData<LocalTransform>(TargetEntity).Scale;

        // 首帧或大位移,直接吸附
        Vector3 currentPos = targetEntityPosition;
        if (!_initialized || (currentPos - transform.position).sqrMagnitude > TeleportSnapDistance * TeleportSnapDistance)
        {
            transform.SetPositionAndRotation(currentPos, targetEntityRotation);
            transform.localScale = new Vector3(targetEntityUniformScale, targetEntityUniformScale, targetEntityUniformScale);
            _lastRenderPosition = currentPos;
            _initialized = true;

            // 清零速度
            if (_animator != null)
                _animator.SetFloat(SpeedParameterName, 0f);

            return;
        }

        transform.SetPositionAndRotation(currentPos, targetEntityRotation);
        transform.localScale = new Vector3(targetEntityUniformScale, targetEntityUniformScale, targetEntityUniformScale);

        // 仅对 Animator 的 Speed 做指数平滑
        if (_animator != null)
        {
            float distance = (currentPos - _lastRenderPosition).magnitude;
            if (SpeedDeadbandMeters > 0f && distance < SpeedDeadbandMeters)
                distance = 0f;

            float rawSpeed = distance / Mathf.Max(Time.deltaTime, 1e-5f);

            float currentAnimatorSpeed = _animator.GetFloat(SpeedParameterName);
            float k = 1f - Mathf.Exp(-SpeedSmoothingStrength * Mathf.Max(Time.deltaTime, 0f));
            float smoothedSpeed = Mathf.Lerp(currentAnimatorSpeed, rawSpeed, k);

            _animator.SetFloat(SpeedParameterName, smoothedSpeed);
        }

        _lastRenderPosition = currentPos;

        _appliedPos = transform.position;
        _appliedRot = transform.rotation;
    }
}
