using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// Blaster 攻击视图层：
/// 1. 监听 ECS 上的 AniAttackFireRequest（ShotId） -> 触发 Animator 的 Shoot 动画
/// 2. 在动画事件 OnShootFire 中：
///    - 用 IK 对准当前目标
///    - 从枪口发射 Raycast，只与 Ground + 敌方 Ani 碰撞体相交
///    - 生成 Beam 特效（可接对象池）
///    - 把命中结果通过 AniHitBridge 回传 ECS
/// </summary>
[DisallowMultipleComponent]
public class BlasterAniAttackView : MonoBehaviour
{
    [Header("ECS 绑定")]
    public Entity TargetEntity;
    public EntityManager BoundEntityManager;

    [Tooltip("这是不是 Server World 里的视图（只有 Server 才驱动伤害）")]
    public bool IsServerWorld = true;

    [Header("Animator & 参数")]
    public Animator Animator;
    [Tooltip("动画里用于开火的 Trigger 名")]
    public string ShootTriggerName = "Shoot";
    [Tooltip("用作上半身 Mask 的 Bool，可选")]
    public string IsShootingBoolName = "IsShooting";

    [Header("IK 绑定")]
    public Transform LeftHandIKTarget;
    public Transform RightHandIKTarget;
    public Transform GunMuzzle;      // 枪口世界位置

    [Range(0f, 1f)]
    public float IKPositionWeight = 0.7f;
    [Range(0f, 1f)]
    public float IKRotationWeight = 0.7f;

    [Header("激光 & 射线检测")]
    public GameObject BeamPrefab;     // 预制体，提前在 Inspector 里拖，不要 Resources.Load
    public LayerMask LaserHitMask;    // 只勾 Ground + 敌 Ani 的 Collider 所在 Layer
    public float MaxLaserDistance = 15f;
    public float BeamLifetime = 0.1f;

    private int _shootTriggerHash;
    private int _isShootingBoolHash;

    private uint _lastConsumedShotId;      // 已经驱动过动画的 ShotId
    private uint _lastFiredVisualShotId;   // 已经真正开过激光特效的 ShotId
    private bool _bound;
    private bool _isShooting;         // 控制 OnAnimatorIK 是否生效

    private void Awake()
    {
        if (!Animator)
        {
            Animator = GetComponentInChildren<Animator>();
        }

        if (!string.IsNullOrEmpty(ShootTriggerName))
            _shootTriggerHash = Animator.StringToHash(ShootTriggerName);
        if (!string.IsNullOrEmpty(IsShootingBoolName))
            _isShootingBoolHash = Animator.StringToHash(IsShootingBoolName);
    }

    /// <summary>
    /// 由生成系统在实例化 View 后调用
    /// </summary>
    public void Bind(Entity entity, EntityManager entityManager, bool isServerWorld = true)
    {
        TargetEntity = entity;
        BoundEntityManager = entityManager;
        IsServerWorld = isServerWorld;
        _bound = true;
    }

    private void Update()
    {
        // Debug.Log($"[BlasterAniAttackView] {name} Update checking for AniAttackFireRequest Bound: {_bound}, BoundEntityManager: {BoundEntityManager}, TargetEntity: {TargetEntity.Index}"
        // + "HasComponent: " + (BoundEntityManager != null && BoundEntityManager.HasComponent<AniAttackFireRequest>(TargetEntity)).ToString());

        if (!_bound || BoundEntityManager == null)
            return;

        if (!BoundEntityManager.Exists(TargetEntity))
            return;

        if (!BoundEntityManager.HasComponent<AniAttackFireRequest>(TargetEntity))
            return;

        var fireRequest = BoundEntityManager.GetComponentData<AniAttackFireRequest>(TargetEntity);

        // ShotId 没变说明这发已经触发过动画了
        if (fireRequest.ShotId == 0 || fireRequest.ShotId == _lastConsumedShotId)
            return;

        // Debug.Log($"[BlasterAniAttackView] {name} received ShotId {fireRequest.ShotId}, triggering shoot animation");

        _lastConsumedShotId = fireRequest.ShotId;

        TriggerShootAnimation();
    }

    private void TriggerShootAnimation()
    {
        if (!Animator)
            return;

        if (_shootTriggerHash != 0)
        {
            Animator.ResetTrigger(_shootTriggerHash);
            Animator.SetTrigger(_shootTriggerHash);
        }

        _isShooting = true;

        if (_isShootingBoolHash != 0)
        {
            Animator.SetBool(_isShootingBoolHash, true);
        }
    }

    // ————————————————————————————
    // Animator IK：只在 _isShooting 为 true 时做手部 IK
    // 不再每帧查 StateName，开销更小，逻辑也更清晰
    // ————————————————————————————
    private void OnAnimatorIK(int layerIndex)
    {
        if (!_isShooting || !Animator)
            return;

        if (LeftHandIKTarget)
        {
            Animator.SetIKPosition(AvatarIKGoal.LeftHand, LeftHandIKTarget.position);
            Animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, IKPositionWeight);
            Animator.SetIKRotation(AvatarIKGoal.LeftHand, LeftHandIKTarget.rotation);
            Animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, IKRotationWeight);
        }

        if (RightHandIKTarget)
        {
            Animator.SetIKPosition(AvatarIKGoal.RightHand, RightHandIKTarget.position);
            Animator.SetIKPositionWeight(AvatarIKGoal.RightHand, IKPositionWeight);
            Animator.SetIKRotation(AvatarIKGoal.RightHand, RightHandIKTarget.rotation);
            Animator.SetIKRotationWeight(AvatarIKGoal.RightHand, IKRotationWeight);
        }
    }

    // ————————————————————————————
    // 动画事件：在“开火帧”上调用这个函数
    // AttackClip 上加一个 Event：函数名 OnShootFire
    // ————————————————————————————
    public void OnShootFire()
    {
        if (!_bound || BoundEntityManager == null)
            return;

        if (!BoundEntityManager.Exists(TargetEntity))
            return;

        if (!BoundEntityManager.HasComponent<AniAttackFireRequest>(TargetEntity))
            return;

        var fireRequest = BoundEntityManager.GetComponentData<AniAttackFireRequest>(TargetEntity);
        uint shotId = fireRequest.ShotId;

        // 没有有效 ShotId，直接丢
        if (shotId == 0)
            return;

        // 1）先确保这个动画事件对应的是“当前这发子弹”（防止过期动画事件）
        if (shotId != _lastConsumedShotId)
        {
            // Debug.Log($"[BlasterAniAttackView] OnShootFire ignored. shotId={shotId}, _lastConsumedShotId={_lastConsumedShotId}");
            return;
        }

        // 2）再防止“同一发子弹的 OnShootFire 被调用多次”（多事件、多层动画之类）
        if (shotId == _lastFiredVisualShotId)
        {
            // Debug.Log($"[BlasterAniAttackView] OnShootFire duplicate for shotId={shotId}, skip FireLaser.");
            return;
        }

        _lastFiredVisualShotId = shotId;

        FireLaser(shotId);
    }
    public void OnShootAnimationEnd()
    {
        _isShooting = false;

        if (Animator && _isShootingBoolHash != 0)
        {
            Animator.SetBool(_isShootingBoolHash, false);
        }
    }

    // 真正执行 Raycast + Beam 特效 + 回传 ECS 的地方
    private void FireLaser(uint shotId)
    {
        Debug.Log($"[BlasterAniAttackView] FireLaser from instance {GetInstanceID()}, name={name}, ShotId={shotId}");

        if (!GunMuzzle)
        {
            Debug.LogWarning($"[BlasterAniAttackView] {name} 未设置 GunMuzzle，无法发射激光");
            return;
        }

        Vector3 origin    = GunMuzzle.position;
        Vector3 direction = GunMuzzle.forward;
        float maxDist = MaxLaserDistance; // 不再依赖 AniAttributes.AttackRange

        RaycastHit hitInfo;
        bool hit = Physics.Raycast(
            origin,
            direction,
            out hitInfo,
            maxDist,
            LaserHitMask,
            QueryTriggerInteraction.Collide);

        Vector3 endPos = hit ? hitInfo.point : origin + direction * maxDist;

        if (BeamPrefab != null)
        {
            Quaternion rot = Quaternion.LookRotation(direction, Vector3.up);
            var fx = Instantiate(BeamPrefab, origin, rot, transform);
            Destroy(fx, BeamLifetime);
        }

        Entity hitTargetEntity = Entity.Null;

        if (hit)
        {
            var follower = hitInfo.collider.GetComponentInParent<AvatarViewFollower>();
            if (follower != null)
            {
                hitTargetEntity = follower.TargetEntity;  // 客户端的 Ghost Entity
            }
        }

        var hitResult = new AniHitResultData
        {
            Attacker    = TargetEntity,
            HitTarget   = hitTargetEntity,
            HitPosition = (float3)endPos,
            HitNormal   = hit ? (float3)hitInfo.normal : (float3)(-direction),
            Damage      = 10,
            AttackMode  = AniAttackMode.Ranged,
            ShotId      = shotId
        };

        // Debug.Log($"[BlasterAniAttackView] Event enqueued. {name} FireLaser ShotId {shotId}, Hit.Name:{hitInfo.collider.name} HitTarget: {hitTargetEntity}, HitPosition: {hitResult.HitPosition}");

        AniHitBridge.Enqueue(hitResult);
    }
}
