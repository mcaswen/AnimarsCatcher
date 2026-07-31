#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using AnimarsCatcher.Networking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AnimarsCatcher.Editor
{
    /// <summary>
    /// 在批处理 Editor 中打开固定场景、进入 Play Mode 并等待基准结果
    /// </summary>
    [InitializeOnLoad]
    public static class LegacyNavigationBenchmarkBatchRunner
    {
        private const string ActiveKey = "AnimarsCatcher.LegacyBenchmark.Active";
        private const string AgentCountKey = "AnimarsCatcher.LegacyBenchmark.AgentCount";
        private const string StartTimeKey = "AnimarsCatcher.LegacyBenchmark.StartTime";
        private const string PendingExitCodeKey = "AnimarsCatcher.LegacyBenchmark.PendingExitCode";
        private const int NoPendingExitCode = int.MinValue;
        private const double TimeoutSeconds = 300.0;

        private static double _pollStartTime;

        static LegacyNavigationBenchmarkBatchRunner()
        {
            EditorApplication.delayCall += ExitIfPending;
            EditorApplication.delayCall += ResumeIfActive;
        }

        /// <summary>
        /// 在无人值守模式运行 32 Ani Legacy 基准
        /// </summary>
        public static void Run32FromCommandLine()
        {
            BeginRun(32);
        }

        /// <summary>
        /// 在无人值守模式运行 64 Ani Legacy 基准
        /// </summary>
        public static void Run64FromCommandLine()
        {
            BeginRun(64);
        }

        /// <summary>
        /// 在无人值守模式运行 128 Ani Legacy 基准
        /// </summary>
        public static void Run128FromCommandLine()
        {
            BeginRun(128);
        }

        private static void BeginRun(int agentCount)
        {
            if (Application.isBatchMode &&
                !NetworkPlayModeConfiguration.IsServerOnly)
            {
                throw new InvalidOperationException(
                    "批处理 Legacy Benchmark 必须使用 -benchmark-server-only 启动参数");
            }

            string scenePath =
                $"Assets/Scenes/Benchmarks/LegacyNavigation/" +
                $"SCN_LegacyNavigationBenchmark_{agentCount}.unity";
            if (!File.Exists(Path.GetFullPath(scenePath)))
            {
                throw new FileNotFoundException("Benchmark Scene 不存在", scenePath);
            }

            SessionState.SetBool(ActiveKey, true);
            SessionState.SetInt(AgentCountKey, agentCount);
            SessionState.SetString(
                StartTimeKey,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            ResumeIfActive();
            EditorApplication.EnterPlaymode();
        }

        private static void ResumeIfActive()
        {
            if (!SessionState.GetBool(ActiveKey, false))
            {
                return;
            }

            _pollStartTime = EditorApplication.timeSinceStartup;
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
        }

        private static void ExitIfPending()
        {
            int exitCode = SessionState.GetInt(PendingExitCodeKey, NoPendingExitCode);
            if (exitCode == NoPendingExitCode)
            {
                return;
            }

            SessionState.SetInt(PendingExitCodeKey, NoPendingExitCode);
            EditorApplication.Exit(exitCode);
        }

        private static void Poll()
        {
            if (!SessionState.GetBool(ActiveKey, false))
            {
                EditorApplication.update -= Poll;
                return;
            }

            if (EditorApplication.timeSinceStartup - _pollStartTime > TimeoutSeconds)
            {
                Finish(1, "等待 Legacy Benchmark 结果超时");
                return;
            }

            int agentCount = SessionState.GetInt(AgentCountKey, 0);
            DateTime startTime = DateTime.Parse(
                SessionState.GetString(StartTimeKey, DateTime.UtcNow.ToString("O")),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            string outputDirectory = Path.GetFullPath(
                "BenchmarkResults/LegacyNavigation");
            if (!Directory.Exists(outputDirectory))
            {
                return;
            }

            string prefix = $"LegacyNavigation_{agentCount}_";
            string[] files = Directory.GetFiles(outputDirectory, prefix + "*.json");
            for (int i = 0; i < files.Length; i++)
            {
                if (File.GetLastWriteTimeUtc(files[i]) < startTime)
                {
                    continue;
                }

                Finish(0, $"Legacy Benchmark 结果已生成：{files[i]}");
                return;
            }
        }

        private static void Finish(int exitCode, string message)
        {
            SessionState.SetBool(ActiveKey, false);
            EditorApplication.update -= Poll;

            if (exitCode == 0)
            {
                Debug.Log(message);
            }
            else
            {
                Debug.LogError(message);
            }

            if (Application.isBatchMode)
            {
                if (EditorApplication.isPlaying)
                {
                    SessionState.SetInt(PendingExitCodeKey, exitCode);
                    EditorApplication.ExitPlaymode();
                    return;
                }

                EditorApplication.Exit(exitCode);
                return;
            }

            if (EditorApplication.isPlaying)
            {
                EditorApplication.ExitPlaymode();
            }
        }
    }
}
#endif
