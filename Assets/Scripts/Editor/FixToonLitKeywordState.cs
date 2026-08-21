#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Editor
{
    /// <summary>
    /// 重新同步 ToonLitURP 材质的本地关键字状态
    /// 用于修复材质导入后关键字缓存与 Shader 声明不一致的问题
    /// </summary>
    public static class FixToonLitKeywordState {

        [MenuItem("Tools/Materials/Resync Local Keywords (Custom/ToonLitURP)")]
        static void Run() {

            var shader = Shader.Find("Custom/ToonLitURP");
            if (!shader) { Debug.LogError("Shader not found"); return; }

            // 全项目材质都要检查，避免遗漏不在当前场景引用链上的资产
            foreach (var guid in AssetDatabase.FindAssets("t:Material")) {

                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);

                // 只修改目标 Shader 的材质，其他本地关键字状态保持原样
                if (mat && mat.shader == shader) {

                    // 临时切换 Shader 以强制 Unity 重建材质的本地关键字缓存
                    var err = Shader.Find("Hidden/InternalErrorShader");
                    var original = mat.shader;
                    mat.shader = err;  mat.shader = original;


                    // 清除关键字后由材质属性和后续导入流程重新启用有效项
                    foreach (var kw in shader.keywordSpace.keywords)
                        mat.SetKeyword(kw, false);

            // 标记修改后由末尾的 SaveAssets 统一保存，减少逐个材质写入磁盘
                    EditorUtility.SetDirty(mat);
                }
            }

            // 所有材质完成重建后一次保存并刷新导入状态
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            Debug.Log("Resync done.");
        }
    }
}
#endif
