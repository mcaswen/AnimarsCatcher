#if UNITY_EDITOR
using System;
using AnimarsCatcher.Gameplay.Contracts;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimarsCatcher.Gameplay.Editor
{
    /// <summary>
    /// 验证 Gameplay 程序集归属和 Unity 序列化引用完整性
    /// </summary>
    public static class GameplayAssemblyMigrationValidation
    {
        private const string GameplayAssemblyName = "AnimarsCatcher.Gameplay";
        private const string ContractsAssemblyName = "AnimarsCatcher.Gameplay.Contracts";

        /// <summary>
        /// 验证 Gameplay 程序集归属以及场景和 Prefab 脚本引用
        /// </summary>
        public static void RunFromCommandLine()
        {
            SceneSetup[] sceneSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                ValidateAssemblyOwnership();
                ValidateScenes();
                ValidatePrefabs();
                Debug.Log("Gameplay 程序集迁移验收通过");
            }
            finally
            {
                RestoreSceneSetup(sceneSetup);
            }
        }

        private static void ValidateAssemblyOwnership()
        {
            AssertAssembly(typeof(AniAttributes), GameplayAssemblyName);
            AssertAssembly(typeof(ServerSpawnAnisSystem), GameplayAssemblyName);
            AssertAssembly(typeof(ServerBaseDefeatSystem), GameplayAssemblyName);
            AssertAssembly(typeof(PlayerResourceState), GameplayAssemblyName);
            AssertAssembly(typeof(DebugAdjustResourceRpc), ContractsAssemblyName);
        }

        private static void ValidateScenes()
        {
            string[] sceneGuids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets/Scenes" });
            for (int index = 0; index < sceneGuids.Length; index++)
            {
                string scenePath = AssetDatabase.GUIDToAssetPath(sceneGuids[index]);
                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                int missingScriptCount = CountMissingScripts(scene.GetRootGameObjects());
                Assert(
                    missingScriptCount == 0,
                    $"场景包含 {missingScriptCount} 个 Missing Script: {scenePath}");
            }
        }

        private static void ValidatePrefabs()
        {
            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefabs" });
            for (int index = 0; index < prefabGuids.Length; index++)
            {
                string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuids[index]);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Assert(prefab != null, $"无法加载 Prefab: {prefabPath}");

                int missingScriptCount = CountMissingScripts(new[] { prefab });
                Assert(
                    missingScriptCount == 0,
                    $"Prefab 包含 {missingScriptCount} 个 Missing Script: {prefabPath}");
            }
        }

        private static int CountMissingScripts(GameObject[] roots)
        {
            int missingScriptCount = 0;
            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                Transform[] transforms = roots[rootIndex].GetComponentsInChildren<Transform>(true);
                for (int transformIndex = 0; transformIndex < transforms.Length; transformIndex++)
                {
                    missingScriptCount += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(
                        transforms[transformIndex].gameObject);
                }
            }

            return missingScriptCount;
        }

        private static void AssertAssembly(Type type, string expectedAssemblyName)
        {
            string actualAssemblyName = type.Assembly.GetName().Name;
            Assert(
                actualAssemblyName == expectedAssemblyName,
                $"类型 {type.FullName} 位于 {actualAssemblyName} 而不是 {expectedAssemblyName}");
        }

        private static void RestoreSceneSetup(SceneSetup[] sceneSetup)
        {
            bool hasLoadedScene = Array.Exists(sceneSetup, setup => setup.isLoaded);
            if (hasLoadedScene)
            {
                EditorSceneManager.RestoreSceneManagerSetup(sceneSetup);
                return;
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
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
