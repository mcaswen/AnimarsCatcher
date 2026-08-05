using System.Collections;
using UnityEngine;
using UnityEngine.Serialization;
using Cinemachine;
using AnimarsCatcher.Presentation.InputLock;
using AnimarsCatcher.Player;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 播放战斗场景开场运镜并在演出期间锁定输入和 HUD
    /// </summary>
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
                    // 使用归一化路径位置使演出时长不依赖路径单位
                    _trackedDolly.m_PositionUnits = CinemachinePathBase.PositionUnits.Normalized;
                }
            }
        }

        private void Start()
        {
            ClientCinematicState.ShouldRunIntro = false;
            StartCoroutine(RunCinematic());
        }

        // 提升开场相机优先级并沿轨道推进，完成后恢复输入和 HUD
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

                            // 按演出时间将轨道位置从零平滑推进到一
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

        // 通过共享 UI 输入锁桥接演出状态，避免直接依赖玩家输入系统
        private void SetInputEnabled(bool enabled)
        {
            if (enabled)
                UIInputEvents.RaiseUnlocked();
            else
                UIInputEvents.RaiseLocked();
        }
    }
}
