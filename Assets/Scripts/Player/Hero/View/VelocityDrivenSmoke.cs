namespace AnimarsCatcher.Player
{
    using UnityEngine;

    /// <summary>
    /// 根据表现对象位移速度控制尾烟方向和发射状态
    /// </summary>
    [DisallowMultipleComponent]
    public class VelocityDrivenSmoke : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("用于发射尾烟，通常绑定当前物体的子对象")]
        public ParticleSystem SmokeParticleSystem;

        [Header("发射控制")]
        [Tooltip("速度不高于该值时停止发射，单位米/秒")]
        public float minSpeedToEmit = 0.1f;

        [Tooltip("单帧位移超过该值时按瞬移处理并停止发射，单位米")]
        public float teleportSnapDistance = 2.0f;

        private Vector3 _lastPosition;
        private bool _hasLastPosition;

        private ParticleSystem.EmissionModule _emission;
        private bool _emissionInitialized;

        private void Awake()
        {
            if (SmokeParticleSystem != null)
            {
                _emission = SmokeParticleSystem.emission;
                _emissionInitialized = true;

                // 保持粒子系统播放，仅切换 emission 可保留已发射粒子的自然消散
                if (!SmokeParticleSystem.isPlaying)
                {
                    SmokeParticleSystem.Play();
                }
                _emission.enabled = false;
            }
        }

        private void LateUpdate()
        {
            if (SmokeParticleSystem == null || !_emissionInitialized)
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
            if (delta.sqrMagnitude > teleportSnapDistance * teleportSnapDistance)
            {
                _lastPosition = currentPosition;
                ControlSmokeParticleSystem(Vector3.zero);
                return;
            }

            // 视觉层按渲染帧位移计算速度，避免依赖预测实体的回滚速度
            Vector3 speed = delta / deltaTime;

            ControlSmokeParticleSystem(speed);

            _lastPosition = currentPosition;
        }

        private void ControlSmokeParticleSystem(Vector3 speed)
        {
            if (SmokeParticleSystem == null || !_emissionInitialized)
                return;

            float sqrSpeed    = speed.sqrMagnitude;
            float sqrMinSpeed = minSpeedToEmit * minSpeedToEmit;

            if (sqrSpeed <= sqrMinSpeed)
            {
                // 只关闭发射模块，让已存在粒子继续完成生命周期
                _emission.enabled = false;
            }
            else
            {
                Vector3 backwardDir = -speed.normalized;
                SmokeParticleSystem.transform.forward = backwardDir;

                if (!SmokeParticleSystem.isPlaying)
                {
                    SmokeParticleSystem.Play();
                }

                // 恢复发射前确保粒子系统仍处于播放状态
                _emission.enabled = true;
            }
        }
    }
}
