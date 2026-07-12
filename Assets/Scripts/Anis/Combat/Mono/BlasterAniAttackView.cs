using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// 监听 ECS 攻击序号并驱动 Blaster 动画、手部 IK 和激光表现
/// 动画事件只产生候选射线结果，最终伤害由服务器 ECS 结算
/// </summary>
[DisallowMultipleComponent]
public class BlasterAniAttackView : MonoBehaviour
{
    [Header("ECS 绑定")]
    public Entity TargetEntity;
    public EntityManager BoundEntityManager;

    public bool IsServerWorld = true;

    [Header("Animator & 参数")]
    public Animator Animator;
    [Tooltip("留空则不设置 Trigger，非空需匹配 Animator 参数名")]
    public string ShootTriggerName = "Shoot";
    [Tooltip("可留空，非空需匹配 Animator Bool 参数名")]
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
    private World _boundWorld;
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
    /// 绑定视图对应的 ECS 实体和世界生命周期
    /// </summary>
    /// <param name="entity">视图跟随的网络实体</param>
    /// <param name="entityManager">实体所属世界的管理器</param>
    /// <param name="isServerWorld">视图是否属于服务器世界</param>
    public void Bind(Entity entity, EntityManager entityManager, bool isServerWorld = true)
    {
        TargetEntity = entity;
        BoundEntityManager = entityManager;
        _boundWorld = entityManager.World;
        IsServerWorld = isServerWorld;
        _bound = true;
    }

    private void Update()
    {
        if (!_bound || _boundWorld == null || !_boundWorld.IsCreated)
            return;

        if (!BoundEntityManager.Exists(TargetEntity))
            return;

        if (!BoundEntityManager.HasComponent<AniAttackFireRequest>(TargetEntity))
            return;

        var fireRequest = BoundEntityManager.GetComponentData<AniAttackFireRequest>(TargetEntity);

        // ShotId 同时承担新事件检测和重复消费保护
        if (fireRequest.ShotId == 0 || fireRequest.ShotId == _lastConsumedShotId)
            return;

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

    // 仅在攻击动画期间更新手部 IK，避免空闲帧持续写 Animator
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

    /// <summary>
    /// 由攻击动画开火帧调用，并保证每个 ShotId 只发射一次激光
    /// </summary>
    public void OnShootFire()
    {
        if (!_bound || _boundWorld == null || !_boundWorld.IsCreated)
            return;

        if (!BoundEntityManager.Exists(TargetEntity))
            return;

        if (!BoundEntityManager.HasComponent<AniAttackFireRequest>(TargetEntity))
            return;

        var fireRequest = BoundEntityManager.GetComponentData<AniAttackFireRequest>(TargetEntity);
        uint shotId = fireRequest.ShotId;

        // 零值表示尚未收到有效服务器开火请求
        if (shotId == 0)
            return;

        // 过期动画事件不能结算为当前攻击
        if (shotId != _lastConsumedShotId)
        {
            return;
        }

        // 多动画层或重复事件不能让同一攻击产生多次射线
        if (shotId == _lastFiredVisualShotId)
        {
            return;
        }

        _lastFiredVisualShotId = shotId;

        FireLaser(shotId);
    }

    /// <summary>
    /// 由攻击动画结束帧调用并关闭持续 IK 状态
    /// </summary>
    public void OnShootAnimationEnd()
    {
        _isShooting = false;

        if (Animator && _isShootingBoolHash != 0)
        {
            Animator.SetBool(_isShootingBoolHash, false);
        }
    }

    // 从枪口生成射线与光束，并把候选命中加入 ECS 桥接队列
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
        float maxDist = MaxLaserDistance; // 表现射线使用视图配置的最大可见距离

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
                hitTargetEntity = follower.TargetEntity;  // 桥接到当前世界中的 Ghost 实体
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
        AniHitBridge.Enqueue(hitResult);
    }
}
