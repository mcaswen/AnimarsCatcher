namespace AnimarsCatcher.Presentation.EntityView
{
    using UnityEngine;
    using UnityEngine.Serialization;
    using UnityEngine.Scripting.APIUpdating;

    /// <summary>
    /// 根据表现对象位移速度控制尾烟方向和发射状态
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.PlayerView", "AnimarsCatcher.Presentation", "VelocityDrivenSmoke")]
    [DisallowMultipleComponent]
    public class VelocityDrivenSmoke : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("用于发射尾烟，通常绑定当前物体的子对象")]
        [FormerlySerializedAs("SmokeParticleSystem")]
        [SerializeField] private ParticleSystem _smokeParticleSystem;

        [Header("发射控制")]
        [Tooltip("速度不高于该值时停止发射，单位米/秒")]
        [FormerlySerializedAs("minSpeedToEmit")]
        [SerializeField] private float _minimumSpeedToEmit = 0.1f;

        [Tooltip("单帧位移超过该值时按瞬移处理并停止发射，单位米")]
        [FormerlySerializedAs("teleportSnapDistance")]
        [SerializeField] private float _teleportSnapDistance = 2.0f;

        private Vector3 _lastPosition;
        private bool _hasLastPosition;

        private ParticleSystem.EmissionModule _emission;
        private bool _emissionInitialized;

        private void Awake()
        {
            if (_smokeParticleSystem != null)
            {
                _emission = _smokeParticleSystem.emission;
                _emissionInitialized = true;

                // 保持粒子系统播放，仅切换 emission 可保留已发射粒子的自然消散
                if (!_smokeParticleSystem.isPlaying)
                {
                    _smokeParticleSystem.Play();
                }
                _emission.enabled = false;
            }
        }

        private void LateUpdate()
        {
            if (_smokeParticleSystem == null || !_emissionInitialized)
                return;

            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f)
                return;

            Vector3 currentPosition = transform.position;

            // 第一帧缺少上一帧位置，不能计算有效速度
            if (!_hasLastPosition)
            {
                _lastPosition = currentPosition;
                _hasLastPosition = true;
                return;
            }

            Vector3 delta = currentPosition - _lastPosition;

            // 瞬移距离不应转化为速度，否则会产生异常强烈的尾烟
            if (delta.sqrMagnitude > _teleportSnapDistance * _teleportSnapDistance)
            {
                _lastPosition = currentPosition;
                ControlSmokeParticleSystem(Vector3.zero);
                return;
            }

            // 视觉层按渲染帧位移计算速度，避免依赖预测 Entity 的回滚速度
            Vector3 speed = delta / deltaTime;

            ControlSmokeParticleSystem(speed);

            _lastPosition = currentPosition;
        }

        private void ControlSmokeParticleSystem(Vector3 speed)
        {
            if (_smokeParticleSystem == null || !_emissionInitialized)
                return;

            float sqrSpeed    = speed.sqrMagnitude;
            float squaredMinimumSpeed = _minimumSpeedToEmit * _minimumSpeedToEmit;

            if (sqrSpeed <= squaredMinimumSpeed)
            {
                // 只关闭发射模块，让已存在粒子继续完成生命周期
                _emission.enabled = false;
            }
            else
            {
                Vector3 backwardDir = -speed.normalized;
                _smokeParticleSystem.transform.forward = backwardDir;

                if (!_smokeParticleSystem.isPlaying)
                {
                    _smokeParticleSystem.Play();
                }

                // 恢复发射前确保粒子系统仍处于播放状态
                _emission.enabled = true;
            }
        }
    }
}
