#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using AnimarsCatcher.Benchmarks.LegacyNavigation.Harness;
using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Networking;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimarsCatcher.Editor
{
    /// <summary>
    /// 验证阶段零后端互斥、确定性回放和单场景参数加载
    /// </summary>
    public static class LegacyNavigationBenchmarkStageZeroValidation
    {
        private const string SceneDirectory =
            "Assets/Scenes/Benchmarks/LegacyNavigation";
        private const string ScenePath =
            SceneDirectory + "/SCN_LegacyNavigationBenchmark.unity";

        [MenuItem("Tools/Animars Catcher/Navigation/Run Legacy Benchmark Stage Zero Validation")]
        private static void RunFromMenu()
        {
            RunAll();
        }

        /// <summary>
        /// 供 Unity 批处理执行阶段零结构与确定性验收
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAll();
        }

        /// <summary>
        /// 执行后端、回放、128 Ani 容量和场景夹具验收
        /// </summary>
        public static void RunAll()
        {
            TestLaunchArgumentParsing();
            TestBackendTagsAreExclusive();
            TestConflictingTagsAreRejected();
            TestDeterministicSpawnFor128Anis();
            TestBenchmarkSceneLoader();
            Debug.Log("Legacy Navigation 阶段零自动验收通过");
        }

        private static void TestLaunchArgumentParsing()
        {
            Assert(
                AniMovementBackendLaunchConfiguration.Parse(
                    new[] { "player", "-movement-backend=grid" }) ==
                AniMovementBackend.ClearanceGrid,
                "grid 启动参数解析错误");
            Assert(
                AniMovementBackendLaunchConfiguration.Parse(
                    new[] { "player", "-movement-backend", "legacy" }) ==
                AniMovementBackend.LegacyNavMesh,
                "legacy 启动参数解析错误");
        }

        private static void TestBackendTagsAreExclusive()
        {
            using var world = new World("Backend Exclusivity Validation", WorldFlags.GameServer);
            Entity configEntity = AniMovementBackendWorldUtility.ConfigureWorld(
                world,
                AniMovementBackend.LegacyNavMesh);
            Assert(
                world.EntityManager.HasComponent<LegacyNavMeshBackendEnabled>(configEntity),
                "Legacy 配置必须创建 Legacy Tag");
            Assert(
                !world.EntityManager.HasComponent<GridMovementBackendEnabled>(configEntity),
                "Legacy 配置不能保留 Grid Tag");

            AniMovementBackendWorldUtility.ConfigureWorld(
                world,
                AniMovementBackend.ClearanceGrid);
            Assert(
                world.EntityManager.HasComponent<GridMovementBackendEnabled>(configEntity),
                "Grid 配置必须创建 Grid Tag");
            Assert(
                !world.EntityManager.HasComponent<LegacyNavMeshBackendEnabled>(configEntity),
                "Grid 配置不能保留 Legacy Tag");
            Assert(
                AniMovementBackendWorldUtility.TryValidateWorld(world, out _),
                "合法后端配置必须通过互斥验证");
        }

        private static void TestConflictingTagsAreRejected()
        {
            using var world = new World("Backend Conflict Validation", WorldFlags.GameServer);
            AniMovementBackendWorldUtility.ConfigureWorld(
                world,
                AniMovementBackend.LegacyNavMesh);
            world.EntityManager.CreateEntity(typeof(GridMovementBackendEnabled));

            Assert(
                !AniMovementBackendWorldUtility.TryValidateWorld(world, out string reason),
                "两个后端 Tag 同时存在时必须拒绝配置");
            Assert(
                reason.Contains("同时存在", StringComparison.Ordinal),
                "冲突原因必须明确指出两个后端同时存在");
        }

        private static void TestDeterministicSpawnFor128Anis()
        {
            const int Count = 128;
            var firstRun = new HashSet<int2>();
            for (int index = 0; index < Count; index++)
            {
                float3 first = LegacyNavigationBenchmarkAlgorithms.CalculateSpawnPosition(
                    index,
                    Count,
                    16,
                    1.25f,
                    new float3(105f, 0.57f, 44.43f),
                    104729);
                float3 second = LegacyNavigationBenchmarkAlgorithms.CalculateSpawnPosition(
                    index,
                    Count,
                    16,
                    1.25f,
                    new float3(105f, 0.57f, 44.43f),
                    104729);
                Assert(math.all(first == second), "相同种子必须生成完全相同的 Ani 位置");

                int2 quantized = (int2)math.round(first.xz * 10000f);
                Assert(firstRun.Add(quantized), "128 Ani 生成位置不能重复");
            }
        }

        private static void TestBenchmarkSceneLoader()
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Assert(scene.IsValid(), $"无法打开 Benchmark Scene：{ScenePath}");

                LegacyNavigationBenchmarkController sceneLoader =
                    UnityEngine.Object.FindFirstObjectByType<LegacyNavigationBenchmarkController>();
                Assert(sceneLoader != null, $"{ScenePath} 缺少测试场景加载器");
                Assert(sceneLoader.ReplayScript != null, $"{ScenePath} 缺少回放资产");
                Assert(!string.IsNullOrWhiteSpace(sceneLoader.MapSceneHash), $"{ScenePath} 缺少地图 Hash");

                int[] expectedCounts = { 32, 64, 128 };
                for (int i = 0; i < expectedCounts.Length; i++)
                {
                    int expectedCount = expectedCounts[i];
                    sceneLoader.ConfigureRun(expectedCount);
                    Assert(sceneLoader.AgentCount == expectedCount, $"场景加载器未应用 {expectedCount} Ani 参数");
                }

                for (int i = 0; i < expectedCounts.Length; i++)
                {
                    string legacyScenePath =
                        $"{SceneDirectory}/SCN_LegacyNavigationBenchmark_{expectedCounts[i]}.unity";
                    Assert(
                        !File.Exists(Path.GetFullPath(legacyScenePath)),
                        $"不应继续保留按规模复制的 Benchmark Scene：{legacyScenePath}");
                }
            }
            finally
            {
                if (previousSetup != null &&
                    Array.Exists(previousSetup, setup => setup.isLoaded))
                {
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
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
