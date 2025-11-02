using UnityEngine;


namespace AnimarsCatcher.Mono.Audio
{
    public class AudioSpy : MonoBehaviour
    {
        [ContextMenu("Dump Playing AudioSources")]
        void Dump()
        {
            var sources = FindObjectsByType<AudioSource>(FindObjectsSortMode.None);
            foreach (var src in sources)
            {
                if (!src.isPlaying) continue;
                string path = GetPath(src.transform);
                string clip = src.clip ? src.clip.name : "(OneShot/Unknown)";
                Debug.LogWarning($"[AUDIO] {path}  clip={clip}  loop={src.loop}  output={src.outputAudioMixerGroup?.name}");
            }
        }

        string GetPath(Transform t)
        {
            System.Text.StringBuilder sb = new();
            while (t != null) { sb.Insert(0, "/" + t.name); t = t.parent; }
            return sb.ToString();
        }

        void Start() { Dump(); } // 进场自动 dump 一次
    }
}