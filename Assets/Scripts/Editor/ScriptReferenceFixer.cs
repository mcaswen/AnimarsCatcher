#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Editor
{
    /// <summary>
    /// 清理当前已加载对象上的丢失脚本引用
    /// 该工具直接修改序列化组件列表 使用前应确认场景和 Prefab 已备份
    /// </summary>
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
                    if (comp == null) // Unity 将丢失脚本组件解析为 null
                    {
                        SerializedObject so = new SerializedObject(go);
                        var prop = so.FindProperty("m_Component");
                        for (int i = 0; i < prop.arraySize; i++)
                        {
                            var element = prop.GetArrayElementAtIndex(i);
                            if (element.objectReferenceValue == null)
                            {
                                // 对象引用数组第一次删除会先清空引用 第二次才移除槽位
                                prop.DeleteArrayElementAtIndex(i);
                                prop.DeleteArrayElementAtIndex(i);
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
}
#endif
