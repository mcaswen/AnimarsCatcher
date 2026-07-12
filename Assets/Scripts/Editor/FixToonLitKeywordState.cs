#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// 重新同步 ToonLitURP 材质的本地关键字状态
/// 用于修复材质导入后关键字缓存与 Shader 声明不一致的问题
/// </summary>
public static class FixToonLitKeywordState {
    
    /// <summary>
    /// 扫描项目材质并重建目标 Shader 的关键字状态
    /// </summary>
    [MenuItem("Tools/Materials/Resync Local Keywords (Custom/ToonLitURP)")]
    static void Run() {
        
        var shader = Shader.Find("Custom/ToonLitURP");
        if (!shader) { Debug.LogError("Shader not found"); return; }

        foreach (var guid in AssetDatabase.FindAssets("t:Material")) {
            
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            
            if (mat && mat.shader == shader) {

                // 临时切换 Shader 以强制 Unity 重建材质的本地关键字缓存
                var err = Shader.Find("Hidden/InternalErrorShader");
                var original = mat.shader;
                mat.shader = err;  mat.shader = original;

                
                // 清除关键字后由材质属性和后续导入流程重新启用有效项
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
