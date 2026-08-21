namespace AnimarsCatcher.Presentation.EntityView
{
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.Transforms;
    using UnityEngine;
    using UnityEngine.Serialization;
    using UnityEngine.Scripting.APIUpdating;

    /// <summary>
    /// 让托管表现对象跟随 ECS Entity 并驱动移动动画
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.PlayerView", "AnimarsCatcher.Presentation", "AvatarViewFollower")]
    [DisallowMultipleComponent]
    public class EntityViewFollower : MonoBehaviour
    {
        private Entity _targetEntity;
        private EntityManager _boundEntityManager;

        [Tooltip("Animator 中接收移动速度的 Float 参数名")]
        [FormerlySerializedAs("SpeedParameterName")]
        [SerializeField] private string _speedParameterName = "Speed";

        [Tooltip("值越大动画速度响应越快，需大于 0")]
        [FormerlySerializedAs("SpeedSmoothingStrength")]
        [SerializeField] private float _speedSmoothingStrength = 12f;

        [Tooltip("位移超过该值时按瞬移处理并将动画速度清零，单位米")]
        [FormerlySerializedAs("TeleportSnapDistance")]
        [SerializeField] private float _teleportSnapDistance = 2.0f;

        [Tooltip("单帧位移低于该值时动画速度归零，单位米，0 表示禁用")]
        [FormerlySerializedAs("SpeedDeadbandMeters")]
        [SerializeField] private float _speedDeadbandMeters;

        private Animator _animator;
        private World _boundWorld;
        private bool _initialized;
        private bool _isBound;
        private Vector3 _lastRenderPosition;

        private Vector3 _appliedPosition;
        private Quaternion _appliedRotation;


        /// <summary>
        /// 绑定需要跟随的 Entity 及其所属 EntityManager
        /// </summary>
        /// <param name="entity">目标 Entity</param>
        /// <param name="entityManager">目标 Entity 所属世界的 EntityManager</param>
        public void Bind(Entity entity, EntityManager entityManager)
        {
            _targetEntity = entity;
            _boundEntityManager = entityManager;
            _boundWorld = entityManager.World;
            _initialized = false;
            _isBound = true;
        }

        private void Awake()
        {
            _animator = GetComponentInChildren<Animator>();
            if (_animator != null)
                _animator.applyRootMotion = false;

            _lastRenderPosition = transform.position;
        }

        // 在渲染帧末同步 Entity 姿态并更新移动动画参数
        private void LateUpdate()
        {
            if (!_isBound)
            {
                return;
            }

            // 所属 World 已销毁或目标 Entity 失效时同步回收托管视图
            if (_boundWorld == null || !_boundWorld.IsCreated || !_boundEntityManager.Exists(_targetEntity))
            {
                Destroy(gameObject);
                return;
            }

            // 生成标记被移除表示 Entity 表现生命周期已经结束
            if (!_boundEntityManager.HasComponent<EntityViewSpawnedTag>(_targetEntity))
            {
                Destroy(gameObject);
                return;
            }

            float3 targetEntityPosition;
            quaternion targetEntityRotation;

            var ltw = _boundEntityManager.GetComponentData<LocalToWorld>(_targetEntity);
            float4x4 localToWorldMatrix = ltw.Value;

            // LocalToWorld 的三个列向量同时包含轴向和各轴缩放
            float3 right   = localToWorldMatrix.c0.xyz;
            float3 up      = localToWorldMatrix.c1.xyz;
            float3 forward = localToWorldMatrix.c2.xyz;

            // 从 LocalToWorld 列向量长度恢复非均匀缩放
            float3 scale;
            scale.x = math.length(right);
            scale.y = math.length(up);
            scale.z = math.length(forward);

            targetEntityPosition = _boundEntityManager.GetComponentData<LocalTransform>(_targetEntity).Position;
            targetEntityRotation = _boundEntityManager.GetComponentData<LocalTransform>(_targetEntity).Rotation;

            // 首帧或大位移直接吸附，避免出生和传送时表现对象缓慢追赶
            Vector3 currentPosition = targetEntityPosition;
            if (!_initialized || (currentPosition - transform.position).sqrMagnitude > _teleportSnapDistance * _teleportSnapDistance)
            {
                transform.SetPositionAndRotation(currentPosition, targetEntityRotation);
                transform.localScale = new Vector3(scale.x, scale.y, scale.z);
                _lastRenderPosition = currentPosition;
                _initialized = true;

                // 吸附帧不应把传送距离误判为移动速度
                if (_animator != null)
                    _animator.SetFloat(_speedParameterName, 0f);

                return;
            }

            transform.SetPositionAndRotation(currentPosition, targetEntityRotation);
            transform.localScale = new Vector3(scale.x, scale.y, scale.z);

            // 只平滑 Animator 速度，Entity 位置保持逐帧精确跟随
            if (_animator != null)
            {
                float distance = (currentPosition - _lastRenderPosition).magnitude;
                if (_speedDeadbandMeters > 0f && distance < _speedDeadbandMeters)
                    distance = 0f;

                float rawSpeed = distance / Mathf.Max(Time.deltaTime, 1e-5f);

                float currentAnimatorSpeed = _animator.GetFloat(_speedParameterName);
                float k = 1f - Mathf.Exp(-_speedSmoothingStrength * Mathf.Max(Time.deltaTime, 0f));
                float smoothedSpeed = Mathf.Lerp(currentAnimatorSpeed, rawSpeed, k);

                _animator.SetFloat(_speedParameterName, smoothedSpeed);
            }

            _lastRenderPosition = currentPosition;

            _appliedPosition = transform.position;
            _appliedRotation = transform.rotation;
        }
    }
}
