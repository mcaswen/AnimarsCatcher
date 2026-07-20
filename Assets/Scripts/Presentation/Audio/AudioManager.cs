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

        [FormerlySerializedAs("AudioMixer")]
        [SerializeField] private AudioMixer _audioMixer;
        private AudioSource _uiAudioSource;

        [FormerlySerializedAs("MenuBtnClick")]
        [FormerlySerializedAs("MenuButtonClickClip")]
        [SerializeField] private AudioClip _menuButtonClickClip;
        [FormerlySerializedAs("SwitchBtnClick")]
        [FormerlySerializedAs("SwitchButtonClickClip")]
        [SerializeField] private AudioClip _switchButtonClickClip;

        [FormerlySerializedAs("MasterVolumeScrollbar")]
        [SerializeField] private Scrollbar _masterVolumeScrollbar;
        [FormerlySerializedAs("BGMVolumeScrollbar")]
        [SerializeField] private Scrollbar _bgmVolumeScrollbar;
        [FormerlySerializedAs("UIVolumeScrollbar")]
        [SerializeField] private Scrollbar _uiVolumeScrollbar;

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

            _masterVolumeScrollbar.onValueChanged.AddListener(value =>
            {
                _audioMixer.SetFloat("MasterVolume", Mathf.Lerp(-80f, 20f, value));
            });
            _bgmVolumeScrollbar.onValueChanged.AddListener(value =>
            {
                _audioMixer.SetFloat("BGMVolume", Mathf.Lerp(-80f, 20f, value));
            });
            _uiVolumeScrollbar.onValueChanged.AddListener(value =>
            {
                _audioMixer.SetFloat("UIVolume", Mathf.Lerp(-80f, 20f, value));
            });

            _masterVolumeScrollbar.value = 0.5f;
            _bgmVolumeScrollbar.value = 0.5f;
            _uiVolumeScrollbar.value = 0.5f;
        }

        /// <summary>
        /// 播放菜单按钮点击音效
        /// </summary>
        public void PlayMenuButtonAudio()
        {
            _uiAudioSource.PlayOneShot(_menuButtonClickClip);
        }

        /// <summary>
        /// 播放开关控件音效
        /// </summary>
        public void PlaySwitchButtonAudio()
        {
            _uiAudioSource.PlayOneShot(_switchButtonClickClip);
        }

        /// <summary>
        /// 降低游戏声道音量以突出菜单声音
        /// </summary>
        public void EnterMenu()
        {
            _audioMixer.SetFloat("GameVolume", -30f);
        }

        /// <summary>
        /// 恢复游戏声道音量
        /// </summary>
        public void ExitMenu()
        {
            _audioMixer.SetFloat("GameVolume", 0f);
        }
    }
}

