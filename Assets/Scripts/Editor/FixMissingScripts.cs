#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public class ScriptReferenceFixer : MonoBehaviour
{
    [MenuItem("Tools/强制修复脚本引用")]
    static void FixMissingScripts()
    {
        foreach (GameObject go in FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            var components = go.GetComponents<Component>();
            foreach (var comp in components)
            {
                if (comp == null) // 检测到Missing脚本
                {
                    SerializedObject so = new SerializedObject(go);
                    var prop = so.FindProperty("m_Component");
                    for (int i = 0; i < prop.arraySize; i++)
                    {
                        var element = prop.GetArrayElementAtIndex(i);
                        if (element.objectReferenceValue == null)
                        {
                            prop.DeleteArrayElementAtIndex(i);
                            prop.DeleteArrayElementAtIndex(i); // Unity需要删除两次
                            so.ApplyModifiedProperties();
                            Debug.Log($"已修复 {go.name} 上的丢失脚本");
                            break;
                        }
                    }
                }
            }
        }
    }
}
#endif