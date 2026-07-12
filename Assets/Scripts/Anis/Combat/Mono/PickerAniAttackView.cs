using UnityEngine;
using Unity.Entities;
using Unity.NetCode;

/// <summary>
/// 监听 ECS 攻击序号并驱动 Picker 近战动画事件
/// 视图只确认动画命中时机，最终伤害由服务器 ECS 结算
/// </summary>
[DisallowMultipleComponent]
public class PickerAniAttackView : MonoBehaviour
{
    [Header("ECS 绑定")]
    public Entity TargetEntity;
    public EntityManager BoundEntityManager;

    [Header("Animator 参数名")]
    public string AttackTriggerName   = "Attack";

    // 记录最近消费的攻击序号，避免同一请求重复触发动画
    private uint _lastConsumedShotId;

    private Animator _animator;
    private bool _bound;
    private World _boundWorld;
    public bool IsServerWorld = true;

    private void Awake()
    {
        // 兼容 Animator 挂在根节点或模型子节点的预制体结构
        _animator = GetComponent<Animator>();
        if (_animator == null)
        {
            _animator = GetComponentInChildren<Animator>();
        }
    }

    /// <summary>
    /// 绑定视图对应的 ECS 实体和世界生命周期
    /// </summary>
    /// <param name="entity">视图跟随的网络实体</param>
    /// <param name="entityManager">实体所属世界的管理器</param>
    /// <param name="isServerWorld">视图是否属于服务器世界</param>
    public void Bind(Entity entity, EntityManager entityManager, bool isServerWorld = true)
    {
        TargetEntity  = entity;
        BoundEntityManager = entityManager;
        _boundWorld = entityManager.World;
        _bound  = true;
        IsServerWorld = isServerWorld;
    }

    private void Update()
    {
        if (!_bound || _boundWorld == null || !_boundWorld.IsCreated)
            return;

        if (!BoundEntityManager.Exists(TargetEntity))
            return;

        // 没有服务器开火请求时不驱动表现
        if (!BoundEntityManager.HasComponent<AniAttackFireRequest>(TargetEntity))
            return;

        var fire = BoundEntityManager.GetComponentData<AniAttackFireRequest>(TargetEntity);

        // ShotId 同时承担新事件检测和重复消费保护
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
            // 重置旧触发器，避免快速攻击时 Animator 队列堆积
            _animator.ResetTrigger(AttackTriggerName);
            _animator.SetTrigger(AttackTriggerName);
        }
    }

    /// <summary>
    /// 由近战动画命中帧调用并上报当前攻击序号
    /// </summary>
    public void OnAttackHit()
    {

        if (!_bound || _boundWorld == null || !_boundWorld.IsCreated)
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
