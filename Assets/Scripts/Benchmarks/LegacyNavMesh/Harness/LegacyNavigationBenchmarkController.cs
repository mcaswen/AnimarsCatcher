using System;
using System.Collections;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation.Harness
{
    /// <summary>
    /// 把 Benchmark Scene 的固定配置注册到 Server World
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LegacyNavigationBenchmarkController : MonoBehaviour
    {
        [SerializeField] private int _agentCount = 32;
        [SerializeField] private int _warmupTicks = 120;
        [SerializeField] private int _sampleTicks = 720;
        [SerializeField] private int _spawnColumnCount = 16;
        [SerializeField] private float _spawnSpacing = 1.25f;
        [SerializeField] private Vector3 _spawnOrigin = new(105f, 0.57f, 44.43f);
        [SerializeField] private LegacyNavigationBaselineVariant _baselineVariant =
            LegacyNavigationBaselineVariant.NormalizedLegacy;
        [SerializeField] private string _baselineVersion = "NormalizedLegacy-v1";
        [SerializeField] private string _scenarioName = "LegacyNavigation";
        [SerializeField] private string _mapSceneHash;
        [SerializeField] private string _resultDirectory = "BenchmarkResults/LegacyNavigation";
        [SerializeField] private bool _autoQuitInBatchMode = true;
        [SerializeField] private LegacyNavigationBenchmarkReplayScript _replayScript;

        private bool _registered;

        public int AgentCount => _agentCount;
        public string MapSceneHash => _mapSceneHash;
        public LegacyNavigationBenchmarkReplayScript ReplayScript => _replayScript;

        private IEnumerator Start()
        {
            if (!ValidateConfiguration())
            {
                yield break;
            }

            while (!_registered)
            {
                World serverWorld = ClientServerBootstrap.ServerWorld;
                if (serverWorld != null && serverWorld.IsCreated)
                {
                    Register(serverWorld);
                    yield break;
                }

                yield return null;
            }
        }

        /// <summary>
        /// 写入由固定夹具生成器维护的场景参数
        /// </summary>
        /// <param name="agentCount">场景 Ani 数量</param>
        /// <param name="mapSceneHash">共享地图 Scene 的 SHA256</param>
        /// <param name="replayScript">三个规模场景共用的回放资产</param>
        public void ConfigureFixture(
            int agentCount,
            string mapSceneHash,
            LegacyNavigationBenchmarkReplayScript replayScript)
        {
            _agentCount = agentCount;
            _scenarioName = $"LegacyNavigation_{agentCount}";
            _mapSceneHash = mapSceneHash;
            _replayScript = replayScript;
        }

        private bool ValidateConfiguration()
        {
            if (_agentCount != 32 && _agentCount != 64 && _agentCount != 128)
            {
                Debug.LogError($"[LegacyNavigationBenchmark] Ani 数量必须为 32、64 或 128，当前为 {_agentCount}");
                return false;
            }

            if (_replayScript == null || _replayScript.Commands.Count == 0)
            {
                Debug.LogError("[LegacyNavigationBenchmark] 缺少命令回放资产或回放内容为空");
                return false;
            }

            int previousTick = -1;
            for (int i = 0; i < _replayScript.Commands.Count; i++)
            {
                int tick = _replayScript.Commands[i].Tick;
                if (tick <= previousTick || tick < 0 || tick >= _sampleTicks)
                {
                    Debug.LogError("[LegacyNavigationBenchmark] 回放命令必须按采样范围内的 Tick 严格递增");
                    return false;
                }

                previousTick = tick;
            }

            return true;
        }

        private void Register(World serverWorld)
        {
            EntityManager entityManager = serverWorld.EntityManager;
            using EntityQuery existingQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<LegacyNavigationBenchmarkConfig>());
            if (!existingQuery.IsEmptyIgnoreFilter)
            {
                Debug.LogError("[LegacyNavigationBenchmark] Server World 已存在 Benchmark 配置");
                return;
            }

            using EntityQuery backendQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniMovementBackendConfig>(),
                ComponentType.ReadOnly<LegacyNavMeshBackendEnabled>());
            if (backendQuery.CalculateEntityCount() != 1)
            {
                Debug.LogError("[LegacyNavigationBenchmark] 当前 World 未唯一启用 Legacy NavMesh 后端");
                serverWorld.QuitUpdate = true;
                return;
            }

            Entity configEntity = entityManager.CreateEntity(
                typeof(LegacyNavigationBenchmarkConfig),
                typeof(LegacyNavigationBenchmarkState),
                typeof(LegacyNavigationBenchmarkCounters),
                typeof(NavigationBenchmarkEnabled));
            entityManager.AddBuffer<LegacyNavigationBenchmarkCommandElement>(configEntity);
            entityManager.AddBuffer<LegacyNavigationBenchmarkSampleElement>(configEntity);

            entityManager.SetComponentData(configEntity, new LegacyNavigationBenchmarkConfig
            {
                AgentCount = _agentCount,
                RandomSeed = _replayScript.RandomSeed,
                WarmupTicks = math.max(0, _warmupTicks),
                SampleTicks = math.max(1, _sampleTicks),
                SpawnColumnCount = math.max(1, _spawnColumnCount),
                SpawnSpacing = math.max(0.1f, _spawnSpacing),
                SpawnOrigin = _spawnOrigin,
                BaselineVariant = _baselineVariant,
                BaselineVersion = new FixedString64Bytes(_baselineVersion),
                ScenarioName = new FixedString64Bytes(_scenarioName),
                GitCommit = new FixedString64Bytes(GetGitCommit()),
                MapSceneHash = new FixedString128Bytes(_mapSceneHash),
                ReplayScriptHash = new FixedString128Bytes(_replayScript.ComputeHash()),
                ResultDirectory = new FixedString128Bytes(_resultDirectory),
                AutoQuit = (byte)(_autoQuitInBatchMode ? 1 : 0)
            });
            entityManager.SetComponentData(configEntity, new LegacyNavigationBenchmarkState
            {
                Phase = LegacyNavigationBenchmarkPhase.WaitingForScene,
                LastFormationRotation = quaternion.identity
            });

            DynamicBuffer<LegacyNavigationBenchmarkCommandElement> commandBuffer =
                entityManager.GetBuffer<LegacyNavigationBenchmarkCommandElement>(configEntity);
            for (int i = 0; i < _replayScript.Commands.Count; i++)
            {
                LegacyNavigationBenchmarkCommandDefinition command = _replayScript.Commands[i];
                commandBuffer.Add(new LegacyNavigationBenchmarkCommandElement
                {
                    Tick = command.Tick,
                    TargetOffset = command.TargetOffset
                });
            }

            _registered = true;
            Debug.Log(
                $"[LegacyNavigationBenchmark] 已注册 {_scenarioName}，Ani={_agentCount}，" +
                $"Replay={_replayScript.ComputeHash()}");
        }

        private static string GetGitCommit()
        {
            const string ArgumentPrefix = "-benchmark-git-commit=";
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (arguments[i].StartsWith(ArgumentPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[i][ArgumentPrefix.Length..];
                }
            }

            return "working-tree";
        }
    }
}
