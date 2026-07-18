#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimarsCatcher.Animars.Navigation.Grid.Editor
{
    public static class NavigationAssemblyMigrationValidation
    {
        private const string ScenePath = "Assets/Scenes/Benchmarks/SCN_GridBakeStage1.unity";
        private const string BakeAssetPath =
            "Assets/SO/Navigation/SO_NavigationGrid_SCN_GridBakeStage1.asset";
        private const string AssemblyName = "AnimarsCatcher.Navigation";

        /// <summary>
        /// 验证程序集迁移后的场景脚本与烘焙资产序列化引用
        /// </summary>
        public static void RunFromCommandLine()
        {
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
