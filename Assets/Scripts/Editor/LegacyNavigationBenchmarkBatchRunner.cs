#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using AnimarsCatcher.Benchmarks.LegacyNavigation.Harness;
using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Networking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AnimarsCatcher.Editor
{
    /// <summary>
    /// 在批处理 Editor 中打开固定场景、进入 Play Mode并等待当前导航后端的基准结果
    /// </summary>
    [InitializeOnLoad]
    public static class LegacyNavigationBenchmarkBatchRunner
    {
        private const string ScenePath =
            "Assets/Scenes/Benchmarks/LegacyNavigation/SCN_LegacyNavigationBenchmark.unity";
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
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.delayCall += ExitIfPending;
            }
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
            // 批处理只创建 Server World，避免客户端表现系统污染基准负载
            if (Application.isBatchMode &&
                !NetworkPlayModeConfiguration.IsServerOnly)
            {
                throw new InvalidOperationException(
                    "批处理 Navigation Benchmark 必须使用 -benchmark-server-only 启动参数");
            }

            if (!LegacyNavigationBenchmarkController.IsSupportedAgentCount(agentCount))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(agentCount),
                    agentCount,
                    "Navigation Benchmark 仅支持 32、64 或 128 Ani");
            }

            if (!File.Exists(Path.GetFullPath(ScenePath)))
            {
                throw new FileNotFoundException("Benchmark Scene 不存在", ScenePath);
            }

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            LegacyNavigationBenchmarkController sceneLoader =
                UnityEngine.Object.FindFirstObjectByType<LegacyNavigationBenchmarkController>();
            if (sceneLoader == null)
            {
                throw new InvalidOperationException("Benchmark Scene 缺少测试场景加载器");
            }

            sceneLoader.ConfigureRun(agentCount);
            EditorUtility.SetDirty(sceneLoader);
            // SessionState 跨越 Play Mode 域重载保存无人值守运行进度
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetInt(AgentCountKey, agentCount);
            SessionState.SetString(
                StartTimeKey,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            ResumeIfActive();
            EditorApplication.EnterPlaymode();
        }

        private static void ResumeIfActive()
        {
            if (!SessionState.GetBool(ActiveKey, false))
            {
                return;
            }

            // 先移除旧回调，保证域重载恢复后仍只有一个轮询器
            _pollStartTime = EditorApplication.timeSinceStartup;
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
        }

        private static void ExitIfPending()
        {
            // 必须退出 Play Mode 后再退出 Editor，否则批处理可能丢失最终日志和文件刷新
            int exitCode = SessionState.GetInt(PendingExitCodeKey, NoPendingExitCode);
            if (exitCode == NoPendingExitCode || EditorApplication.isPlayingOrWillChangePlaymode)
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
                Finish(1, "等待 Navigation Benchmark 结果超时");
                return;
            }

            int agentCount = SessionState.GetInt(AgentCountKey, 0);
            DateTime startTime = DateTime.Parse(
                SessionState.GetString(StartTimeKey, DateTime.UtcNow.ToString("O")),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            bool gridBackend =
                AniMovementBackendLaunchConfiguration.Current == AniMovementBackend.ClearanceGrid;
            string outputDirectory = Path.GetFullPath(
                gridBackend
                    ? "BenchmarkResults/GridNavigation"
                    : "BenchmarkResults/LegacyNavigation");
            if (!Directory.Exists(outputDirectory))
            {
                return;
            }

            string prefix = gridBackend
                ? $"GridNavigation_{agentCount}_"
                : $"LegacyNavigation_{agentCount}_";
            string[] files = Directory.GetFiles(outputDirectory, prefix + "*.json");
            for (int i = 0; i < files.Length; i++)
            {
                // 启动前残留的同规模结果不能被误认为本次运行完成
                if (File.GetLastWriteTimeUtc(files[i]) < startTime)
                {
                    continue;
                }

                bool failed = false;
                try
                {
                    // Runner 只读取报告状态，不重新解释移动指标或到达阈值
                    BenchmarkResultStatus status = JsonUtility.FromJson<BenchmarkResultStatus>(
                        File.ReadAllText(files[i]));
                    failed = status != null && status.Failed;
                }
                catch (Exception exception)
                {
                    Finish(1, $"无法读取 Navigation Benchmark 结果：{files[i]}，{exception.Message}");
                    return;
                }

                Finish(
                    failed ? 1 : 0,
                    failed
                        ? $"Navigation Benchmark 报告功能失败：{files[i]}"
                        : $"Navigation Benchmark 结果已生成：{files[i]}");
                return;
            }
        }

        [Serializable]
        private sealed class BenchmarkResultStatus
        {
            public bool Failed;
        }

        private static void Finish(int exitCode, string message)
        {
            // 先关闭轮询再切换 Play Mode，避免域重载后重复消费同一报告
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
                    // 暂存退出码，由回到 Edit Mode 后的 delayCall 完成进程退出
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
