using System.Collections;
using UnityEngine;
using UnityEngine.Serialization;
using Cinemachine;
using AnimarsCatcher.Mono.Global;

public class BattleIntroCinematic : MonoBehaviour
{
    [Header("Cinemachine")]
    [FormerlySerializedAs("introVirtualCamera")]
    [SerializeField] private CinemachineVirtualCamera _introVirtualCamera;
    [FormerlySerializedAs("HealthHUD")]
    [SerializeField] private GameObject _healthHud;
    [FormerlySerializedAs("cinemachineBrain")]
    [SerializeField] private CinemachineBrain _cinemachineBrain;
    [FormerlySerializedAs("cinematicDurationSeconds")]
    [SerializeField] private float _cinematicDurationSeconds = 5f;

    private CinemachineTrackedDolly _trackedDolly;

    private void Awake()
    {
        if (_introVirtualCamera != null)
        {
            _trackedDolly = _introVirtualCamera
                .GetCinemachineComponent<CinemachineTrackedDolly>();

            if (_trackedDolly != null)
            {
                // ★ 关键：PathPosition 用 0~1 范围的归一化单位
                _trackedDolly.m_PositionUnits = CinemachinePathBase.PositionUnits.Normalized;
            }
        }
    }

    private void Start()
    {
        // 只有在 LoadingUI 告诉我们需要运镜时才跑
        // if (!ClientCinematicState.ShouldRunIntro)
        // {
        //     enabled = false;
        //     return;
        // }

        ClientCinematicState.ShouldRunIntro = false;
        StartCoroutine(RunCinematic());
    }

    private IEnumerator RunCinematic()
    {
        ClientCinematicState.IsRunning = true;
        SetInputEnabled(false);
        _healthHud.SetActive(false);
        if (_cinemachineBrain != null)
        {
            _cinemachineBrain.enabled = true;
        }

        if (_introVirtualCamera != null)
        {
            int oldPriority = _introVirtualCamera.Priority;
            _introVirtualCamera.Priority = oldPriority + 100;

            if (_trackedDolly != null && _trackedDolly.m_Path != null)
            {
                _trackedDolly.m_PathPosition = 0f;

                if (_cinematicDurationSeconds <= 0.001f)
                {
                    Debug.LogWarning("[BattleIntroCinematic] cinematicDurationSeconds too small.");
                }
                else
                {
                    float t = 0f;
                    while (t < _cinematicDurationSeconds)
                    {
                        t += Time.deltaTime;
                        float normalized = Mathf.Clamp01(t / _cinematicDurationSeconds);

                        // ★ 从 0 推到 1，Cinemachine 用 Normalized 单位解释
                        _trackedDolly.m_PathPosition = normalized;

                        yield return null;
                    }
                }
            }
            else
            {
                yield return new WaitForSeconds(_cinematicDurationSeconds);
            }

            _introVirtualCamera.Priority = oldPriority;
        }
        else
        {
            yield return new WaitForSeconds(_cinematicDurationSeconds);
        }

        SetInputEnabled(true);
        ClientCinematicState.IsRunning = false;
        _healthHud.SetActive(true);
        if (_cinemachineBrain != null)
        {
            _cinemachineBrain.enabled = false;
        }
    }

    private void SetInputEnabled(bool enabled)
    {
        if (enabled)
            NetUIEventBridge.RaiseUIPanelInputUnlocked();
        else
            NetUIEventBridge.RaiseUIPanelInputLocked();
    }
}
