using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using AnimarsCatcher.Presentation.Audio;
using AnimarsCatcher.Presentation.Global;
using DG.Tweening;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 管理游戏内暂停菜单和音量面板之间的切换
    /// </summary>
    public class GameMenuPanelController: MonoBehaviour
    {
        public GameObject MenuPanel;
        public GameObject VolumeAdjustPanel;

        public Button Button_ReturnGame;
        public Button Button_AdjustVolume;
        public Button Button_QuitGame;
        public Button Button_VolumeConfirm;

        [FormerlySerializedAs("_panelAnimDuration")]
        [SerializeField] private float _panelAnimationDuration = 0.25f;

        // 绑定返回游戏 音量设置和退出命令
        void Start()
        {
            Button_ReturnGame.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                AudioManager.Instance.ExitMenu();
                NetworkUIEventBridge.RaiseUIPanelInputUnlocked();
                SmoothPanelView.HidePanel(MenuPanel, _panelAnimationDuration);
                Time.timeScale = 1;
            });

            Button_AdjustVolume.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                SmoothPanelView.HidePanel(MenuPanel, _panelAnimationDuration);
                SmoothPanelView.ShowPanel(VolumeAdjustPanel, _panelAnimationDuration);
            });

            Button_VolumeConfirm.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                SmoothPanelView.HidePanel(VolumeAdjustPanel, _panelAnimationDuration);
                SmoothPanelView.ShowPanel(MenuPanel, _panelAnimationDuration);
            });

            Button_QuitGame.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                Debug.LogWarning("Quit Game");
                Application.Quit();
            });
        }
    }
}
