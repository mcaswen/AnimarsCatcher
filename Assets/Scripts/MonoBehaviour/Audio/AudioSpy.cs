using UnityEngine;


namespace AnimarsCatcher.Mono.Audio
{
    /// <summary>
    /// 输出当前正在播放的 AudioSource 路径和混音信息
    /// 用于定位场景中重复播放或路由错误的音源
    /// </summary>
    public class AudioSpy : MonoBehaviour
    {
        /// <summary>
        /// 扫描并输出所有正在播放的音源
        /// </summary>
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

        // 从当前节点向根节点拼接稳定的层级路径
        string GetPath(Transform t)
        {
            System.Text.StringBuilder sb = new();
            while (t != null) { sb.Insert(0, "/" + t.name); t = t.parent; }
            return sb.ToString();
        }

        // 进入场景时自动记录一次音频快照
        void Start() { Dump(); }
    }
}
