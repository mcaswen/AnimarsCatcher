using UnityEngine;
using Unity.Entities;
using Unity.NetCode;

// Picker 近战攻击的视图层驱动：
// 只要看到 AniAttackFireRequest 的 ShotId 变化，就给 Animator 打 Attack 触发。
// 逻辑伤害依然完全在 ECS 世界中完成，这里只管表现。
[DisallowMultipleComponent]
public class PickerAniAttackView : MonoBehaviour
{
    [Header("ECS 绑定")]
    public Entity TargetEntity;
    public EntityManager BoundEntityManager;

    [Header("Animator 参数名")]
    public string AttackTriggerName   = "Attack";

    // 最近一次已经消费的 ShotId，避免一帧多次触发
    private uint _lastConsumedShotId;

    private Animator _animator;
    private bool _bound;
    public bool IsServerWorld = true;

    private void Awake()
    {
        // 尝试在自己或子节点上找 Animator
        _animator = GetComponent<Animator>();
        if (_animator == null)
        {
            _animator = GetComponentInChildren<Animator>();
        }
    }

    /// <summary>
    /// 由生成系统在实例化 View 后调用，绑定对应 ECS 实体。
    /// </summary>
    public void Bind(Entity entity, EntityManager entityManager, bool isServerWorld = true)
    {
        TargetEntity  = entity;
        BoundEntityManager = entityManager;
        _bound  = true;
        IsServerWorld = isServerWorld;
    }

    private void Update()
    {
        if (!_bound || BoundEntityManager == null)
            return;

        if (!BoundEntityManager.Exists(TargetEntity))
            return;

        // 没有开火请求，就什么也不做
        if (!BoundEntityManager.HasComponent<AniAttackFireRequest>(TargetEntity))
            return;

        var fire = BoundEntityManager.GetComponentData<AniAttackFireRequest>(TargetEntity);

        // ShotId 没变说明这一发已经消费过了
        if (fire.ShotId == 0 || fire.ShotId == _lastConsumedShotId)
            return;

        _lastConsumedShotId = fire.ShotId;

        PlayAttackAnimation();
    }

    private void PlayAttackAnimation()
    {
        if (_animator == null)
            return;

        if (!string.IsNullOrEmpty(AttackTriggerName))
        {
            // 防止上一次没播完又叠触发导致 Animator 内部队列堆积
            _animator.ResetTrigger(AttackTriggerName);
            _animator.SetTrigger(AttackTriggerName);
        }
    }

    public void OnAttackHit()
    {

        if (!_bound || BoundEntityManager == null)
            return;

        if (!BoundEntityManager.Exists(TargetEntity))
            return;

        if (_lastConsumedShotId == 0)
            return;

        var evtData = new AniAttackHitEvent
        {
            Attacker = TargetEntity,
            ShotId   = _lastConsumedShotId
        };

        AniAttackEventBridge.Enqueue(evtData);
    }
    
}
