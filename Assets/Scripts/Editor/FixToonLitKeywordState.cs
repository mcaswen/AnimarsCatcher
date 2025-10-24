#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public static class FixToonLitKeywordState {
    
    [MenuItem("Tools/Materials/Resync Local Keywords (Custom/ToonLitURP)")]
    static void Run() {
        
        var shader = Shader.Find("Custom/ToonLitURP");
        if (!shader) { Debug.LogError("Shader not found"); return; }

        foreach (var guid in AssetDatabase.FindAssets("t:Material")) {
            
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            
            if (mat && mat.shader == shader) {

                var err = Shader.Find("Hidden/InternalErrorShader");
                var original = mat.shader;
                mat.shader = err;  mat.shader = original;

                
                foreach (var kw in shader.keywordSpace.keywords)
                    mat.SetKeyword(kw, false); 

                EditorUtility.SetDirty(mat);
            }
        }
        
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        Debug.Log("Resync done.");
    }
}
#endif
