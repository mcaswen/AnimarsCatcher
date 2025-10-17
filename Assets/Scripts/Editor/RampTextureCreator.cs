using UnityEngine;
using UnityEditor;

public class RampTextureCreator : EditorWindow
{
    [MenuItem("Tools/Create Ramp Texture")]
    static void Init()
    {
        Texture2D rampTexture = new Texture2D(256, 1, TextureFormat.RGBA32, false);
        
        Color[] colors = new Color[256];
        
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
        
        byte[] bytes = rampTexture.EncodeToPNG();
        System.IO.File.WriteAllBytes(Application.dataPath + "/RampTexture.png", bytes);
        AssetDatabase.Refresh();
    }
}