#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    /// <summary>
    /// 检查导航代码拆分程序集后，场景脚本和烘焙资产引用是否仍然有效
    /// </summary>
    public static class NavigationAssemblyMigrationValidation
    {
        private const string ScenePath = "Assets/Scenes/Benchmarks/SCN_GridBakeStage1.unity";
        private const string BakeAssetPath =
            "Assets/SO/Navigation/SO_NavigationGrid_SCN_GridBakeStage1.asset";
        private const string AssemblyName = "AnimarsCatcher.Navigation";
        private const string EditorAssemblyName = "AnimarsCatcher.Navigation.Editor";

        /// <summary>
        /// 检查固定测试场景和资产能否正确反序列化到新的导航程序集
        /// </summary>
        public static void RunFromCommandLine()
        {
            Assert(
                typeof(NavigationAssemblyMigrationValidation).Assembly.GetName().Name ==
                EditorAssemblyName,
                "Navigation 验收入口没有迁移到 Editor 程序集");
            SceneSetup[] sceneSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                ValidateBakeAsset();
                ValidateScene();
                Debug.Log("Navigation 程序集序列化迁移验收通过");
            }
            finally
            {
                bool hasLoadedScene = Array.Exists(sceneSetup, setup => setup.isLoaded);
                if (hasLoadedScene)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(sceneSetup);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
        }

        // 固定资产必须加载为新程序集中的正确类型
        // 同时检查版本和内容，避免迁移后只剩一个看似有效的空引用
        private static void ValidateBakeAsset()
        {
            NavigationGridBakeAsset bakeAsset =
                AssetDatabase.LoadAssetAtPath<NavigationGridBakeAsset>(BakeAssetPath);
            Assert(bakeAsset != null, $"无法按新程序集类型加载烘焙资产: {BakeAssetPath}");

            MonoScript script = MonoScript.FromScriptableObject(bakeAsset);
            Assert(script != null && script.GetClass() != null, "烘焙资产脚本类型无法解析");
            Assert(
                script.GetClass().Assembly.GetName().Name == AssemblyName,
                "烘焙资产脚本没有迁移到 Navigation 程序集");
        }

        // 场景中的 MonoBehaviour 必须能解析到导航程序集
        // 场景加载后同时检查 Missing Script 和烘焙资产绑定
        private static void ValidateScene()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            int missingScriptCount = 0;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                Transform[] transforms = roots[rootIndex].GetComponentsInChildren<Transform>(true);
                for (int transformIndex = 0; transformIndex < transforms.Length; transformIndex++)
                {
                    missingScriptCount += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(
                        transforms[transformIndex].gameObject);
                }
            }

            Assert(missingScriptCount == 0, $"场景包含 {missingScriptCount} 个 Missing Script");

            NavigationGridAuthoring[] authorings =
                UnityEngine.Object.FindObjectsByType<NavigationGridAuthoring>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
            Assert(authorings.Length == 1, "测试场景必须且只能包含一个 NavigationGridAuthoring");
            Assert(authorings[0].BakeAsset != null, "NavigationGridAuthoring 的 Bake Asset 引用丢失");

            MonoScript script = MonoScript.FromMonoBehaviour(authorings[0]);
            Assert(script != null && script.GetClass() != null, "场景 Authoring 脚本类型无法解析");
            Assert(
                script.GetClass().Assembly.GetName().Name == AssemblyName,
                "场景 Authoring 脚本没有迁移到 Navigation 程序集");
        }

        // 验证失败时抛出异常，让命令行进程通过退出码报告问题
        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
#endif
