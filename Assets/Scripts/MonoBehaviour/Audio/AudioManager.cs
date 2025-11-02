using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UI;

namespace AnimarsCatcher.Mono.Audio
{
    public class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        public AudioMixer AudioMixer;
        private AudioSource _uIAudioSource;
        
        public AudioClip MenuBtnClick;
        public AudioClip SwitchBtnClick;

        public Scrollbar MasterVolumeScrollbar;
        public Scrollbar BGMVolumeScrollbar;
        public Scrollbar UIVolumeScrollbar;
        
        private void Awake()
        {
            Instance = this;
            _uIAudioSource = GetComponent<AudioSource>();

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

        public void PlayMenuButtonAudio()
        {
            _uIAudioSource.PlayOneShot(MenuBtnClick);
        }

        public void PlaySwitchBtnAudio()
        {
            _uIAudioSource.PlayOneShot(SwitchBtnClick);
        }

        public void EnterMenu()
        {
            AudioMixer.SetFloat("GameVolume", -30f);
        }

        public void ExitMenu()
        {
            AudioMixer.SetFloat("GameVolume", 0f);
        }
    }
}


