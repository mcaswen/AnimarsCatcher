#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AnimarsCatcher.Benchmarks.LegacyNavigation.Harness;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimarsCatcher.Editor
{
    /// <summary>
    /// 创建阶段零 Legacy 导航回放资产和共享 Benchmark Scene
    /// </summary>
    public static class LegacyNavigationBenchmarkFixtureFactory
    {
        private const string SourceScenePath =
            "Assets/Scenes/Gameplay/SCN_GameLevel.unity";
        private const string SceneDirectory =
            "Assets/Scenes/Benchmarks/LegacyNavigation";
        private const string ScenePath =
            SceneDirectory + "/SCN_LegacyNavigationBenchmark.unity";
        private const string ReplayDirectory =
            "Assets/SO/Benchmarks/LegacyNavigation";
        private const string ReplayAssetPath =
            ReplayDirectory + "/SO_LegacyNavigation_DefaultReplay.asset";

        private static readonly string[] LegacyScaleScenePaths =
        {
            SceneDirectory + "/SCN_LegacyNavigationBenchmark_32.unity",
            SceneDirectory + "/SCN_LegacyNavigationBenchmark_64.unity",
            SceneDirectory + "/SCN_LegacyNavigationBenchmark_128.unity"
        };

        [MenuItem("Tools/Animars Catcher/Navigation/Create Legacy Benchmark Fixtures")]
        private static void CreateFromMenu()
        {
            CreateAll();
        }

        /// <summary>
        /// 供 Unity 批处理创建或刷新阶段零固定夹具
        /// </summary>
        public static void CreateFromCommandLine()
        {
            CreateAll();
        }

        /// <summary>
        /// 创建共享回放资产并保证唯一场景包含正确测试加载器配置
        /// </summary>
        public static void CreateAll()
        {
            EnsureAssetFolder(SceneDirectory);
            EnsureAssetFolder(ReplayDirectory);

            LegacyNavigationBenchmarkReplayScript replayScript = CreateOrUpdateReplayScript();
            string sourceSceneHash = ComputeFileHash(SourceScenePath);
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();

            try
            {
                CreateOrUpdateScene(sourceSceneHash, replayScript);
                RemoveLegacyScaleScenes();
            }
            finally
            {
                RestoreSceneSetup(previousSetup);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Legacy Navigation 阶段零 Benchmark 夹具创建完成");
        }

        private static LegacyNavigationBenchmarkReplayScript CreateOrUpdateReplayScript()
        {
            LegacyNavigationBenchmarkReplayScript replayScript =
                AssetDatabase.LoadAssetAtPath<LegacyNavigationBenchmarkReplayScript>(ReplayAssetPath);
            if (replayScript == null)
            {
                replayScript = ScriptableObject.CreateInstance<LegacyNavigationBenchmarkReplayScript>();
                AssetDatabase.CreateAsset(replayScript, ReplayAssetPath);
            }

            replayScript.Configure(
                104729,
                new[]
                {
                    new LegacyNavigationBenchmarkCommandDefinition(0, new Vector3(-20f, 0f, 16f)),
                    new LegacyNavigationBenchmarkCommandDefinition(180, new Vector3(-42f, 0f, 0f)),
                    new LegacyNavigationBenchmarkCommandDefinition(360, new Vector3(-20f, 0f, -16f)),
                    new LegacyNavigationBenchmarkCommandDefinition(540, Vector3.zero)
                });
            EditorUtility.SetDirty(replayScript);
            return replayScript;
        }

        private static void CreateOrUpdateScene(
            string sourceSceneHash,
            LegacyNavigationBenchmarkReplayScript replayScript)
        {
            if (!File.Exists(Path.GetFullPath(ScenePath)) &&
                !AssetDatabase.CopyAsset(SourceScenePath, ScenePath))
            {
                throw new InvalidOperationException($"无法复制 Benchmark Scene：{ScenePath}");
            }

            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            LegacyNavigationBenchmarkController sceneLoader =
                UnityEngine.Object.FindFirstObjectByType<LegacyNavigationBenchmarkController>();
            if (sceneLoader == null)
            {
                var loaderObject = new GameObject("Legacy Navigation Benchmark Scene Loader");
                sceneLoader = loaderObject.AddComponent<LegacyNavigationBenchmarkController>();
            }

            sceneLoader.gameObject.name = "Legacy Navigation Benchmark Scene Loader";
            sceneLoader.ConfigureFixture(sourceSceneHash, replayScript);
            EditorUtility.SetDirty(sceneLoader);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        private static void RemoveLegacyScaleScenes()
        {
            for (int i = 0; i < LegacyScaleScenePaths.Length; i++)
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(LegacyScaleScenePaths[i]) != null)
                {
                    AssetDatabase.DeleteAsset(LegacyScaleScenePaths[i]);
                }
            }
        }

        private static string ComputeFileHash(string assetPath)
        {
            using SHA256 sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(File.ReadAllBytes(Path.GetFullPath(assetPath)));
            var builder = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }

            return builder.ToString();
        }

        private static void EnsureAssetFolder(string folderPath)
        {
            string[] segments = folderPath.Split('/');
            string currentPath = segments[0];
            for (int index = 1; index < segments.Length; index++)
            {
                string nextPath = currentPath + "/" + segments[index];
                if (!AssetDatabase.IsValidFolder(nextPath))
                {
                    AssetDatabase.CreateFolder(currentPath, segments[index]);
                }

                currentPath = nextPath;
            }
        }

        private static void RestoreSceneSetup(SceneSetup[] previousSetup)
        {
            if (previousSetup != null &&
                Array.Exists(previousSetup, setup => setup.isLoaded))
            {
                EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                return;
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }
}
#endif
