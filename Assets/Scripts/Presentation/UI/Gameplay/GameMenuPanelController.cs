using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using AnimarsCatcher.Presentation.Audio;
using AnimarsCatcher.Presentation.InputLock;
using DG.Tweening;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 管理游戏内暂停菜单和音量面板之间的切换
    /// </summary>
    public class GameMenuPanelController : MonoBehaviour
    {
        [FormerlySerializedAs("MenuPanel")]
        [SerializeField] private GameObject _menuPanel;
        [FormerlySerializedAs("VolumeAdjustPanel")]
        [SerializeField] private GameObject _volumeAdjustPanel;

        [FormerlySerializedAs("Button_ReturnGame")]
        [SerializeField] private Button _returnGameButton;
        [FormerlySerializedAs("Button_AdjustVolume")]
        [SerializeField] private Button _adjustVolumeButton;
        [FormerlySerializedAs("Button_QuitGame")]
        [SerializeField] private Button _quitGameButton;
        [FormerlySerializedAs("Button_VolumeConfirm")]
        [SerializeField] private Button _volumeConfirmButton;

        [FormerlySerializedAs("_panelAnimDuration")]
        [SerializeField] private float _panelAnimationDuration = 0.25f;

        // 绑定返回游戏 音量设置和退出命令
        void Start()
        {
            _returnGameButton.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                AudioManager.Instance.ExitMenu();
                UIInputEvents.RaiseUnlocked();
                SmoothPanelView.HidePanel(_menuPanel, _panelAnimationDuration);
                Time.timeScale = 1;
            });

            _adjustVolumeButton.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                SmoothPanelView.HidePanel(_menuPanel, _panelAnimationDuration);
                SmoothPanelView.ShowPanel(_volumeAdjustPanel, _panelAnimationDuration);
            });

            _volumeConfirmButton.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                SmoothPanelView.HidePanel(_volumeAdjustPanel, _panelAnimationDuration);
                SmoothPanelView.ShowPanel(_menuPanel, _panelAnimationDuration);
            });

            _quitGameButton.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                Debug.LogWarning("Quit Game");
                Application.Quit();
            });
        }
    }
}
