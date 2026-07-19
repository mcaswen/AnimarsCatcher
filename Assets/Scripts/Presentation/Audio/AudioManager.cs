using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace AnimarsCatcher.Presentation.Audio
{
    /// <summary>
    /// 跨场景保留的音量和 UI 音效控制器
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        public AudioMixer AudioMixer;
        private AudioSource _uiAudioSource;

        [FormerlySerializedAs("MenuBtnClick")]
        public AudioClip MenuButtonClickClip;
        [FormerlySerializedAs("SwitchBtnClick")]
        public AudioClip SwitchButtonClickClip;

        public Scrollbar MasterVolumeScrollbar;
        public Scrollbar BGMVolumeScrollbar;
        public Scrollbar UIVolumeScrollbar;

        // 建立唯一实例并跨场景保留音频对象
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(this);
        }

        private void Start()
        {
            _uiAudioSource = GetComponent<AudioSource>();

            MasterVolumeScrollbar.onValueChanged.AddListener(value =>
            {
                AudioMixer.SetFloat("MasterVolume", Mathf.Lerp(-80f, 20f, value));
            });
            BGMVolumeScrollbar.onValueChanged.AddListener(value =>
            {
                AudioMixer.SetFloat("BGMVolume", Mathf.Lerp(-80f, 20f, value));
            });
            UIVolumeScrollbar.onValueChanged.AddListener(value =>
            {
                AudioMixer.SetFloat("UIVolume", Mathf.Lerp(-80f, 20f, value));
            });

            MasterVolumeScrollbar.value = 0.5f;
            BGMVolumeScrollbar.value = 0.5f;
            UIVolumeScrollbar.value = 0.5f;
        }

        /// <summary>
        /// 播放菜单按钮点击音效
        /// </summary>
        public void PlayMenuButtonAudio()
        {
            _uiAudioSource.PlayOneShot(MenuButtonClickClip);
        }

        /// <summary>
        /// 播放开关控件音效
        /// </summary>
        public void PlaySwitchButtonAudio()
        {
            _uiAudioSource.PlayOneShot(SwitchButtonClickClip);
        }

        /// <summary>
        /// 降低游戏声道音量以突出菜单声音
        /// </summary>
        public void EnterMenu()
        {
            AudioMixer.SetFloat("GameVolume", -30f);
        }

        /// <summary>
        /// 恢复游戏声道音量
        /// </summary>
        public void ExitMenu()
        {
            AudioMixer.SetFloat("GameVolume", 0f);
        }
    }
}


