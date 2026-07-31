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
    /// 创建阶段零 Legacy 导航回放资产和固定规模 Benchmark Scene
    /// </summary>
    public static class LegacyNavigationBenchmarkFixtureFactory
    {
        private const string SourceScenePath =
            "Assets/Scenes/Gameplay/SCN_GameLevel.unity";
        private const string SceneDirectory =
            "Assets/Scenes/Benchmarks/LegacyNavigation";
        private const string ReplayDirectory =
            "Assets/SO/Benchmarks/LegacyNavigation";
        private const string ReplayAssetPath =
            ReplayDirectory + "/SO_LegacyNavigation_DefaultReplay.asset";

        private static readonly int[] AgentCounts = { 32, 64, 128 };

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
        /// 创建共享回放资产并保证三个规模场景包含正确 Harness 配置
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
                for (int i = 0; i < AgentCounts.Length; i++)
                {
                    CreateOrUpdateScene(
                        AgentCounts[i],
                        sourceSceneHash,
                        replayScript);
                }
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
            int agentCount,
            string sourceSceneHash,
            LegacyNavigationBenchmarkReplayScript replayScript)
        {
            string scenePath =
                $"{SceneDirectory}/SCN_LegacyNavigationBenchmark_{agentCount}.unity";
            if (!File.Exists(Path.GetFullPath(scenePath)) &&
                !AssetDatabase.CopyAsset(SourceScenePath, scenePath))
            {
                throw new InvalidOperationException($"无法复制 Benchmark Scene：{scenePath}");
            }

            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            LegacyNavigationBenchmarkController controller =
                UnityEngine.Object.FindFirstObjectByType<LegacyNavigationBenchmarkController>();
            if (controller == null)
            {
                var controllerObject = new GameObject("Legacy Navigation Benchmark Harness");
                controller = controllerObject.AddComponent<LegacyNavigationBenchmarkController>();
            }

            controller.ConfigureFixture(agentCount, sourceSceneHash, replayScript);
            EditorUtility.SetDirty(controller);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, scenePath);
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
