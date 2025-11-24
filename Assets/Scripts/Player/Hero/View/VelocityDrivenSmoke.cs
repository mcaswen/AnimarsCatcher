using UnityEngine;

[DisallowMultipleComponent]
public class VelocityDrivenSmoke : MonoBehaviour
{
    [Header("引用")]
    [Tooltip("拖进来尾烟粒子系统（一般是当前物体的子物体）")]
    public ParticleSystem SmokeParticleSystem;

    [Header("发射控制")]
    [Tooltip("低于这个速度视为静止，不发射烟（单位：米/秒）")]
    public float minSpeedToEmit = 0.1f;

    [Tooltip("瞬移距离阈值，大于这个认为是瞬移，直接重置，不发烟")]
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

            // 确保系统处于播放状态，但一开始可以关 emission
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

        Vector3 currentPos = transform.position;

        // 第一帧只记录位置，不计算速度
        if (!_hasLastPosition)
        {
            _lastPosition = currentPos;
            _hasLastPosition = true;
            return;
        }

        Vector3 delta = currentPos - _lastPosition;

        // 瞬移：直接认为没速度，并重置位置
        if (delta.sqrMagnitude > teleportSnapDistance * teleportSnapDistance)
        {
            _lastPosition = currentPos;
            ControlSmokeParticleSystem(Vector3.zero);
            return;
        }

        // 视觉层自己算速度
        Vector3 speed = delta / deltaTime;

        ControlSmokeParticleSystem(speed);

        _lastPosition = currentPos;
    }

    /// <summary>
    /// 有速度时向反方向发粒子，没速度就停。
    /// </summary>
    private void ControlSmokeParticleSystem(Vector3 speed)
    {
        if (SmokeParticleSystem == null || !_emissionInitialized)
            return;

        float sqrSpeed    = speed.sqrMagnitude;
        float sqrMinSpeed = minSpeedToEmit * minSpeedToEmit;

        if (sqrSpeed <= sqrMinSpeed)
        {
            // ❌ 不再 Stop()，只关 emission
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

            // ✅ 开启 emission，让粒子持续喷
            _emission.enabled = true;
        }
    }
}
