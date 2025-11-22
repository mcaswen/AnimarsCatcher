using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AnimarsCatcher.Mono.Audio;
using AnimarsCatcher.Mono.Global;
using DG.Tweening;

namespace AnimarsCatcher.Mono.UI
{
    public class GameMenuPanelController: MonoBehaviour
    {
        public GameObject MenuPanel;
        public GameObject VolumeAdjustPanel;

        public Button Button_ReturnGame;
        public Button Button_AdjustVolume;
        public Button Button_QuitGame;
        public Button Button_VolumeConfirm;

        [SerializeField] private float _panelAnimDuration = 0.25f;

        void Start()
        {
            Button_ReturnGame.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                AudioManager.Instance.ExitMenu();
                NetUIEventBridge.RaiseUIPanelInputUnlocked();
                SmoothPanelView.HidePanel(MenuPanel, _panelAnimDuration);
                Time.timeScale = 1;
            });

            Button_AdjustVolume.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                SmoothPanelView.HidePanel(MenuPanel, _panelAnimDuration);
                SmoothPanelView.ShowPanel(VolumeAdjustPanel, _panelAnimDuration);
            });
            
            Button_VolumeConfirm.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                SmoothPanelView.HidePanel(VolumeAdjustPanel, _panelAnimDuration);
                SmoothPanelView.ShowPanel(MenuPanel, _panelAnimDuration);
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