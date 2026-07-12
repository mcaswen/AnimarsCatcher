using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

/// <summary>让托管角色表现对象跟随 ECS 实体并驱动移动动画</summary>
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
    private bool _isBound;
    private Vector3 _lastRenderPosition;

    private Vector3 _appliedPos;
    private Quaternion _appliedRot;


    /// <summary>绑定需要跟随的实体及其所属 EntityManager</summary>
    /// <param name="entity">目标实体</param>
    /// <param name="entityManager">目标实体所属世界的 EntityManager</param>
    public void Bind(Entity entity, EntityManager entityManager)
    {
        TargetEntity = entity;
        BoundEntityManager = entityManager;
        _initialized = false;
        _isBound = true;
    }

    /// <summary>缓存 Animator 并禁用会与实体同步冲突的 Root Motion</summary>
    private void Awake()
    {
        _animator = GetComponentInChildren<Animator>();
        if (_animator != null)
            _animator.applyRootMotion = false;

        _lastRenderPosition = transform.position;
    }

    /// <summary>在渲染帧末同步实体姿态并更新移动动画参数</summary>
    private void LateUpdate()
    {
        if (!_isBound)
        {
            return;
        }

        // 所属 World 已销毁或目标实体失效时同步回收托管视图
        if (BoundEntityManager == default || !BoundEntityManager.Exists(TargetEntity))
        {
            Destroy(gameObject);
            return;
        }

        if (!BoundEntityManager.Exists(TargetEntity))
        {
            Destroy(gameObject);
            return;
        }

        // 生成标记被移除表示实体表现生命周期已经结束
        if (!BoundEntityManager.HasComponent<AvatarViewSpawnedTag>(TargetEntity))
        {
            Destroy(gameObject);
            return;
        }

        float3 targetEntityPosition;
        quaternion targetEntityRotation;

        var ltw = BoundEntityManager.GetComponentData<LocalToWorld>(TargetEntity);
        float4x4 m = ltw.Value;

        // LocalToWorld 的三个列向量同时包含轴向和各轴缩放
        float3 right   = m.c0.xyz;
        float3 up      = m.c1.xyz;
        float3 forward = m.c2.xyz;

        // 从 LocalToWorld 列向量长度恢复非均匀缩放
        float3 scale;
        scale.x = math.length(right);
        scale.y = math.length(up);
        scale.z = math.length(forward);

        targetEntityPosition = BoundEntityManager.GetComponentData<LocalTransform>(TargetEntity).Position;
        targetEntityRotation = BoundEntityManager.GetComponentData<LocalTransform>(TargetEntity).Rotation;

        // 首帧或大位移直接吸附，避免出生和传送时表现对象缓慢追赶
        Vector3 currentPos = targetEntityPosition;
        if (!_initialized || (currentPos - transform.position).sqrMagnitude > TeleportSnapDistance * TeleportSnapDistance)
        {
            transform.SetPositionAndRotation(currentPos, targetEntityRotation);
            transform.localScale = new Vector3(scale.x, scale.y, scale.z);
            _lastRenderPosition = currentPos;
            _initialized = true;

            // 吸附帧不应把传送距离误判为移动速度
            if (_animator != null)
                _animator.SetFloat(SpeedParameterName, 0f);

            return;
        }

        transform.SetPositionAndRotation(currentPos, targetEntityRotation);
        transform.localScale = new Vector3(scale.x, scale.y, scale.z);

        // 只平滑 Animator 速度，实体位置保持逐帧精确跟随
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
