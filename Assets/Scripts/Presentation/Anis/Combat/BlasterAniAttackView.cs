using AnimarsCatcher.Gameplay;
using AnimarsCatcher.Presentation.Selection;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Serialization;
using UnityEngine.Scripting.APIUpdating;
using Unity.Transforms;

namespace AnimarsCatcher.Presentation.Anis
{
    /// <summary>
    /// 监听 ECS 攻击序号并驱动 Blaster 动画、手部 IK 和激光表现
    /// 动画事件只产生候选射线结果，最终伤害由服务器 ECS 结算
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Gameplay", "AnimarsCatcher.Gameplay", "BlasterAniAttackView")]
    [DisallowMultipleComponent]
    public class BlasterAniAttackView : MonoBehaviour
    {
        private Entity _targetEntity;
        private EntityManager _boundEntityManager;

        [Header("Animator & 参数")]
        [FormerlySerializedAs("Animator")]
        [SerializeField] private Animator _animator;
        [Tooltip("留空则不设置 Trigger，非空需匹配 Animator 参数名")]
        [FormerlySerializedAs("ShootTriggerName")]
        [SerializeField] private string _shootTriggerName = "Shoot";
        [Tooltip("可留空，非空需匹配 Animator Bool 参数名")]
        [FormerlySerializedAs("IsShootingBoolName")]
        [SerializeField] private string _isShootingBoolName = "IsShooting";

        [Header("IK 绑定")]
        [FormerlySerializedAs("LeftHandIKTarget")]
        [SerializeField] private Transform _leftHandIkTarget;
        [FormerlySerializedAs("RightHandIKTarget")]
        [SerializeField] private Transform _rightHandIkTarget;
        [FormerlySerializedAs("GunMuzzle")]
        [SerializeField] private Transform _gunMuzzle;

        [Range(0f, 1f)]
        [FormerlySerializedAs("IKPositionWeight")]
        [SerializeField] private float _ikPositionWeight = 0.7f;
        [Range(0f, 1f)]
        [FormerlySerializedAs("IKRotationWeight")]
        [SerializeField] private float _ikRotationWeight = 0.7f;

        [Header("激光 & 射线检测")]
        [Tooltip("在 Inspector 中绑定，运行时不会从 Resources 加载")]
        [FormerlySerializedAs("BeamPrefab")]
        [SerializeField] private GameObject _beamPrefab;
        [Tooltip("只包含地面和敌方 Ani 的碰撞层")]
        [FormerlySerializedAs("LaserHitMask")]
        [SerializeField] private LayerMask _laserHitMask;
        [FormerlySerializedAs("MaxLaserDistance")]
        [SerializeField] private float _maximumLaserDistance = 15f;
        [FormerlySerializedAs("BeamLifetime")]
        [SerializeField] private float _beamLifetime = 0.1f;

        private int _shootTriggerHash;
        private int _isShootingBoolHash;

        // 分别阻止动画请求和激光事件重复消费同一 ShotId
        private uint _lastConsumedShotId;
        private uint _lastFiredVisualShotId;
        private bool _bound;
        private World _boundWorld;
        private bool _isShooting;

        private void Awake()
        {
            if (!_animator)
            {
                _animator = GetComponentInChildren<Animator>();
            }

            // 可选参数只在初始化时转为 Hash，零值表示对应动画通道未配置
            if (!string.IsNullOrEmpty(_shootTriggerName))
                _shootTriggerHash = Animator.StringToHash(_shootTriggerName);
            if (!string.IsNullOrEmpty(_isShootingBoolName))
                _isShootingBoolHash = Animator.StringToHash(_isShootingBoolName);
        }

        /// <summary>
        /// 绑定视图对应的 ECS 实体和世界生命周期
        /// </summary>
        /// <param name="entity">视图跟随的网络实体</param>
        /// <param name="entityManager">实体所属世界的管理器</param>
        public void Bind(Entity entity, EntityManager entityManager)
        {
            // EntityManager 与 World 成对保存，场景切换后先验证 World 生命周期
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

            if (!_boundEntityManager.HasComponent<AniAttackFireRequest>(_targetEntity))
                return;

            // 请求组件由 ECS 保留，视图通过单调 ShotId 判断是否出现新攻击
            var fireRequest = _boundEntityManager.GetComponentData<AniAttackFireRequest>(_targetEntity);

            // ShotId 同时承担新事件检测和重复消费保护
            if (fireRequest.ShotId == 0 || fireRequest.ShotId == _lastConsumedShotId)
                return;

            _lastConsumedShotId = fireRequest.ShotId;

            TriggerShootAnimation();
        }

        private void TriggerShootAnimation()
        {
            if (!_animator)
                return;

            if (_shootTriggerHash != 0)
            {
                // 先复位可让相邻 Shot 在 Trigger 尚未自动清除时仍重新触发
                _animator.ResetTrigger(_shootTriggerHash);
                _animator.SetTrigger(_shootTriggerHash);
            }

            _isShooting = true;

            if (_isShootingBoolHash != 0)
            {
                _animator.SetBool(_isShootingBoolHash, true);
            }
        }

        // 仅在攻击动画期间更新手部 IK，避免空闲帧持续写 Animator
        private void OnAnimatorIK(int layerIndex)
        {
            if (!_isShooting || !_animator)
                return;

            if (_leftHandIkTarget)
            {
                _animator.SetIKPosition(AvatarIKGoal.LeftHand, _leftHandIkTarget.position);
                _animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, _ikPositionWeight);
                _animator.SetIKRotation(AvatarIKGoal.LeftHand, _leftHandIkTarget.rotation);
                _animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, _ikRotationWeight);
            }

            if (_rightHandIkTarget)
            {
                _animator.SetIKPosition(AvatarIKGoal.RightHand, _rightHandIkTarget.position);
                _animator.SetIKPositionWeight(AvatarIKGoal.RightHand, _ikPositionWeight);
                _animator.SetIKRotation(AvatarIKGoal.RightHand, _rightHandIkTarget.rotation);
                _animator.SetIKRotationWeight(AvatarIKGoal.RightHand, _ikRotationWeight);
            }
        }

        /// <summary>
        /// 由攻击动画开火帧调用，并保证每个 ShotId 只发射一次激光
        /// </summary>
        public void OnShootFire()
        {
            if (!_bound || _boundWorld == null || !_boundWorld.IsCreated)
                return;

            if (!_boundEntityManager.Exists(_targetEntity))
                return;

            if (!_boundEntityManager.HasComponent<AniAttackFireRequest>(_targetEntity))
                return;

            var fireRequest = _boundEntityManager.GetComponentData<AniAttackFireRequest>(_targetEntity);
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

            if (_animator && _isShootingBoolHash != 0)
            {
                _animator.SetBool(_isShootingBoolHash, false);
            }
        }

        // 从枪口生成射线与光束，并把候选命中加入 ECS 桥接队列
        private void FireLaser(uint shotId)
        {
            Debug.Log($"[BlasterAniAttackView] FireLaser from instance {GetInstanceID()}, name={name}, ShotId={shotId}");

            if (!_gunMuzzle)
            {
                Debug.LogWarning($"[BlasterAniAttackView] {name} 未设置 GunMuzzle，无法发射激光");
                return;
            }

            Vector3 origin = _gunMuzzle.position;
            Vector3 direction = _gunMuzzle.forward;
            float maximumDistance = _maximumLaserDistance;

            RaycastHit hitInfo;
            bool hit = Physics.Raycast(
                origin,
                direction,
                out hitInfo,
                maximumDistance,
                _laserHitMask,
                QueryTriggerInteraction.Collide);

            // 未命中仍绘制到最大射程，保证开火反馈完整
            Vector3 endPosition = hit ? hitInfo.point : origin + direction * maximumDistance;

            if (_beamPrefab != null)
            {
                Quaternion rotation = Quaternion.LookRotation(direction, Vector3.up);
                var beam = Instantiate(_beamPrefab, origin, rotation, transform);
                Destroy(beam, _beamLifetime);
            }

            Entity hitTargetEntity = Entity.Null;

            if (hit)
            {
                // Proxy 把场景碰撞体桥接回当前 World 的 Ghost 实体
                var selectableProxy = hitInfo.collider.GetComponentInParent<WorldCommandTargetProxy>();
                if (selectableProxy != null)
                {
                    hitTargetEntity = selectableProxy.Entity;
                }
            }

            // 表现层只提交候选命中，服务器会再次验证 ShotId、目标和伤害资格
            var hitResult = new AniHitResultData
            {
                Attacker = _targetEntity,
                HitTarget   = hitTargetEntity,
                HitPosition = (float3)endPosition,
                HitNormal   = hit ? (float3)hitInfo.normal : (float3)(-direction),
                Damage      = 10,
                AttackMode  = AniAttackMode.Ranged,
                ShotId      = shotId
            };
            AniHitBridge.Enqueue(hitResult);
        }
    }
}
