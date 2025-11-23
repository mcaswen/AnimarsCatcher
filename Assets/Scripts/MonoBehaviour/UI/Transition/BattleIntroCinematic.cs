using System.Collections;
using UnityEngine;
using Cinemachine;
using AnimarsCatcher.Mono.Global;

public class BattleIntroCinematic : MonoBehaviour
{
    [Header("Cinemachine")]
    [SerializeField] private CinemachineVirtualCamera introVirtualCamera;
    [SerializeField] private GameObject HealthHUD;
    [SerializeField] private CinemachineBrain cinemachineBrain;
    [SerializeField] private float cinematicDurationSeconds = 5f;

    private CinemachineTrackedDolly _trackedDolly;

    private void Awake()
    {
        if (introVirtualCamera != null)
        {
            _trackedDolly = introVirtualCamera
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
        HealthHUD.SetActive(false);
        if (cinemachineBrain != null)
        {
            cinemachineBrain.enabled = true;
        }

        if (introVirtualCamera != null)
        {
            int oldPriority = introVirtualCamera.Priority;
            introVirtualCamera.Priority = oldPriority + 100;

            if (_trackedDolly != null && _trackedDolly.m_Path != null)
            {
                _trackedDolly.m_PathPosition = 0f;

                if (cinematicDurationSeconds <= 0.001f)
                {
                    Debug.LogWarning("[BattleIntroCinematic] cinematicDurationSeconds too small.");
                }
                else
                {
                    float t = 0f;
                    while (t < cinematicDurationSeconds)
                    {
                        t += Time.deltaTime;
                        float normalized = Mathf.Clamp01(t / cinematicDurationSeconds);

                        // ★ 从 0 推到 1，Cinemachine 用 Normalized 单位解释
                        _trackedDolly.m_PathPosition = normalized;

                        yield return null;
                    }
                }
            }
            else
            {
                yield return new WaitForSeconds(cinematicDurationSeconds);
            }

            introVirtualCamera.Priority = oldPriority;
        }
        else
        {
            yield return new WaitForSeconds(cinematicDurationSeconds);
        }

        SetInputEnabled(true);
        ClientCinematicState.IsRunning = false;
        HealthHUD.SetActive(true);
        if (cinemachineBrain != null)
        {
            cinemachineBrain.enabled = false;
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
