using UnityEditor;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    class CodeGenMenu
    {
        [MenuItem("Assets/Multiplayer/Force Code Generation", priority = 1000)]
        private static void ForceRunCodeGen()
        {
            EditorApplication.delayCall += () =>
            {
                // 重新导入 NetCode 包
                // 额外问题：如何强制重新编译 DOTSRuntime
                // Template 不属于依赖项，因此尚不清楚如何强制重新编译 DLL
                var obj = AssetDatabase.LoadAssetAtPath<Object>("Packages/com.unity.netcode/Runtime");
                var oldSelection = Selection.activeObject;
                Selection.activeObject = obj;
                try
                {
                    EditorApplication.ExecuteMenuItem("Assets/Reimport");
                }
                finally
                {
                    Selection.activeObject = oldSelection;
                }
            };
        }

        [MenuItem("Assets/Multiplayer/Open Source Generated Folder", priority = 1000)]
        private static void OpenSourceGeneratedFolder()
        {
            if (!System.IO.File.Exists("Temp/NetCodeGenerated"))
            {
                // 创建一个带空日志的占位目录
                System.IO.Directory.CreateDirectory("Temp/NetCodeGenerated");
                System.IO.File.CreateText("Temp/NetCodeGenerated/SourceGenerator.log").Close();
            }
            EditorUtility.RevealInFinder("Temp/NetCodeGenerated/SourceGenerator.log");
        }
    }
}
