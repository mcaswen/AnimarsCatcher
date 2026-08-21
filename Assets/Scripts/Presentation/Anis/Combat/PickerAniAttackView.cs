using AnimarsCatcher.Gameplay;
using UnityEngine;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine.Serialization;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Presentation.Anis
{
    /// <summary>
    /// 监听 ECS 攻击序号并驱动 Picker 近战动画事件
    /// 视图只确认动画命中时机，最终伤害由服务器 ECS 结算
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Gameplay", "AnimarsCatcher.Gameplay", "PickerAniAttackView")]
    [DisallowMultipleComponent]
    public class PickerAniAttackView : MonoBehaviour
    {
        private Entity _targetEntity;
        private EntityManager _boundEntityManager;

        [Header("Animator 参数名")]
        [FormerlySerializedAs("AttackTriggerName")]
        [SerializeField] private string _attackTriggerName = "Attack";

        // 记录最近处理的攻击序号，避免同一请求重复触发动画
        private uint _lastConsumedShotId;

        private Animator _animator;
        private bool _bound;
        private World _boundWorld;

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
        /// 绑定视图对应的 ECS Entity 及其所属 World
        /// </summary>
        /// <param name="entity">视图跟随的网络 Entity</param>
        /// <param name="entityManager">Entity 所属世界的管理器</param>
        public void Bind(Entity entity, EntityManager entityManager)
        {
            _targetEntity = entity;
            _boundEntityManager = entityManager;
            _boundWorld = entityManager.World;
            _bound = true;
        }

        private void Update()
        {
            if (!_bound || _boundWorld == null || !_boundWorld.IsCreated)
                return;

            if (!_boundEntityManager.Exists(_targetEntity))
                return;

            // 没有服务器开火请求时不驱动表现
            if (!_boundEntityManager.HasComponent<AniAttackFireRequest>(_targetEntity))
                return;

            var fire = _boundEntityManager.GetComponentData<AniAttackFireRequest>(_targetEntity);

            // 通过 ShotId 判断是否出现新攻击，并防止重复处理同一次攻击
            if (fire.ShotId == 0 || fire.ShotId == _lastConsumedShotId)
                return;

            _lastConsumedShotId = fire.ShotId;

            PlayAttackAnimation();
        }

        private void PlayAttackAnimation()
        {
            if (_animator == null)
                return;

            if (!string.IsNullOrEmpty(_attackTriggerName))
            {
                // 重置旧触发器，避免快速攻击时 Animator 队列堆积
                _animator.ResetTrigger(_attackTriggerName);
                _animator.SetTrigger(_attackTriggerName);
            }
        }

        /// <summary>
        /// 由近战动画命中帧调用并上报当前攻击序号
        /// </summary>
        public void OnAttackHit()
        {

            if (!_bound || _boundWorld == null || !_boundWorld.IsCreated)
                return;

            if (!_boundEntityManager.Exists(_targetEntity))
                return;

            if (_lastConsumedShotId == 0)
                return;

            var evtData = new AniAttackHitEvent
            {
                Attacker = _targetEntity,
                ShotId   = _lastConsumedShotId
            };

            AniAttackEventBridge.Enqueue(evtData);
        }

    }
}
