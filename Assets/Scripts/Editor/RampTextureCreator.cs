#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace AnimarsCatcher.Editor
{
    /// <summary>
    /// 生成供卡通光照使用的三阶灰度 Ramp 纹理
    /// </summary>
    public class RampTextureCreator : EditorWindow
    {
        [MenuItem("Tools/Create Ramp Texture")]
        static void CreateRampTexture()
        {
            Texture2D rampTexture = new Texture2D(256, 1, TextureFormat.RGBA32, false);

            Color[] colors = new Color[256];

            // 将亮度区间离散为暗部 中间调和亮部三个台阶
            for (int i = 0; i < 256; i++)
            {
                if (i < 85)
                    colors[i] = new Color(0.3f, 0.3f, 0.3f);
                else if (i < 170)
                    colors[i] = new Color(0.6f, 0.6f, 0.6f);
                else
                    colors[i] = Color.white;
            }

            rampTexture.SetPixels(colors);
            rampTexture.Apply();

            // 写入后刷新 AssetDatabase 以便编辑器立即导入资源
            byte[] bytes = rampTexture.EncodeToPNG();
            System.IO.File.WriteAllBytes(Application.dataPath + "/RampTexture.png", bytes);
            AssetDatabase.Refresh();
        }
    }
}
#endif
