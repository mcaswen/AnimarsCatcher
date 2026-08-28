using System;
using System.Collections;
using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Navigation.Grid;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation.Harness
{
    /// <summary>
    /// 从唯一 Benchmark Scene 加载测试参数并注册到 Server World
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

        /// <summary>
        /// 判断 Ani 数量是否属于统一 Harness 登记的测试规模
        /// </summary>
        public static bool IsSupportedAgentCount(int agentCount)
        {
            return NavigationGridBenchmarkScaleProfile.IsSupportedAgentCount(agentCount);
        }

        /// <summary>
        /// 判断 Ani 数量是否属于 Legacy 和严格阵型仍需回放的规模
        /// </summary>
        public static bool IsReplayBaselineAgentCount(int agentCount)
        {
            return NavigationGridBenchmarkScaleProfile.IsReplayBaselineAgentCount(agentCount);
        }

        /// <summary>
        /// 校验命令行选择的 Grid 工作负载与规模组合
        /// </summary>
        public static void ValidateRequestedGridRun(int agentCount)
        {
            NavigationGridBenchmarkWorkload workload = GetGridWorkload();
            if (!NavigationGridBenchmarkScaleProfile.TryValidateRun(
                    workload,
                    agentCount,
                    out string reason))
            {
                throw new ArgumentException(reason, nameof(agentCount));
            }
        }

        /// <summary>
        /// 返回批处理运行器用于核对报告的工作负载名称
        /// </summary>
        public static string GetRequestedGridWorkloadName()
        {
            return GetGridWorkload().ToString();
        }

        private IEnumerator Start()
        {
            // 场景参数不完整时不向稍后创建的 Server World 留下半套配置
            if (!ValidateConfiguration())
            {
                yield break;
            }

            // NetCode World 可能晚于场景 MonoBehaviour 创建，逐帧等待其可写
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
        /// 写入由固定测试数据生成器维护的共享场景参数
        /// </summary>
        /// <param name="mapSceneHash">共享地图 Scene 的 SHA256</param>
        /// <param name="replayScript">所有测试规模共用的回放资产</param>
        public void ConfigureFixture(
            string mapSceneHash,
            LegacyNavigationBenchmarkReplayScript replayScript)
        {
            ConfigureRun(32);
            _mapSceneHash = mapSceneHash;
            _replayScript = replayScript;
        }

        /// <summary>
        /// 在进入 Play Mode 前把本次测试规模写入当前场景加载器
        /// </summary>
        public void ConfigureRun(int agentCount)
        {
            if (!IsSupportedAgentCount(agentCount))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(agentCount),
                    agentCount,
                    "Navigation Benchmark 仅支持 32、64、128、512、1000、2500、5000 或 10000 Ani");
            }

            _agentCount = agentCount;
            _scenarioName = $"LegacyNavigation_{agentCount}";
        }

        private bool ValidateConfiguration()
        {
            if (!IsSupportedAgentCount(_agentCount))
            {
                Debug.LogError(
                    $"[NavigationBenchmark] Ani 数量必须为 32、64、128、512、1000、2500、5000 或 10000，" +
                    $"当前为 {_agentCount}");
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
                // 严格递增可消除同 Tick 多命令的执行次序歧义
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
            using EntityQuery existingGridQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<NavigationGridBenchmarkConfig>());
            // 共享场景一次只允许注册一种后端的测试工作负载
            if (!existingQuery.IsEmptyIgnoreFilter || !existingGridQuery.IsEmptyIgnoreFilter)
            {
                Debug.LogError("[LegacyNavigationBenchmark] Server World 已存在 Benchmark 配置");
                return;
            }

            using EntityQuery backendConfigQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniMovementBackendConfig>());
            // Benchmark 使用哪个后端只由这份唯一配置决定
            if (backendConfigQuery.CalculateEntityCount() != 1)
            {
                Debug.LogError("[NavigationBenchmark] 当前 World 缺少唯一移动后端配置");
                serverWorld.QuitUpdate = true;
                return;
            }

            Entity backendEntity = backendConfigQuery.GetSingletonEntity();
            // Grid 后端复用同一场景加载器，只替换 Server World 内的工作负载
            if (entityManager.HasComponent<GridMovementBackendEnabled>(backendEntity))
            {
                if (!RegisterGridWorkload(entityManager))
                {
                    serverWorld.QuitUpdate = true;
                    return;
                }

                _registered = true;
                Debug.Log(
                    $"[NavigationBenchmark] 已注册 GridNavigation_{_agentCount}，" +
                    $"Ani={_agentCount}，Replay={_replayScript.ComputeHash()}");
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

            if (!IsReplayBaselineAgentCount(_agentCount))
            {
                Debug.LogError(
                    "Legacy Navigation Benchmark 只保留 32、64、128 Ani 回放，" +
                    "阶段六规模必须使用 Grid FreeCohortMovement 或 ScaleInputDeterminism");
                serverWorld.QuitUpdate = true;
                return;
            }

            Entity configEntity = entityManager.CreateEntity(
                typeof(LegacyNavigationBenchmarkConfig),
                typeof(LegacyNavigationBenchmarkState),
                typeof(LegacyNavigationBenchmarkCounters),
                typeof(NavigationBenchmarkEnabled));
            // 配置、状态、计数和样本缓冲集中在单例 Entity，便于各阶段系统共享
            entityManager.AddBuffer<LegacyNavigationBenchmarkCommandElement>(configEntity);
            entityManager.AddBuffer<LegacyNavigationBenchmarkSampleElement>(configEntity);

            // 把 Inspector 参数限制到有效下限，避免错误配置破坏计时流程
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
            // 复制到原生 Buffer 后，运行时不再依赖托管 ReplayScript 资产
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

        private bool RegisterGridWorkload(EntityManager entityManager)
        {
            NavigationGridBenchmarkWorkload workload = GetGridWorkload();
            if (!NavigationGridBenchmarkScaleProfile.TryValidateRun(
                    workload,
                    _agentCount,
                    out string reason))
            {
                Debug.LogError($"[NavigationBenchmark] {reason}");
                return false;
            }

            // Grid 与 Legacy 使用同一组规模、时长、出生布局和回放哈希以保证横向可比
            Entity configEntity = entityManager.CreateEntity(
                typeof(NavigationGridBenchmarkConfig),
                typeof(NavigationGridBenchmarkState),
                typeof(NavigationGridMovementBenchmarkState),
                typeof(NavigationGridScaleInputBenchmarkState),
                typeof(NavigationBenchmarkEnabled));
            entityManager.AddBuffer<NavigationGridBenchmarkCommand>(configEntity);
            entityManager.AddBuffer<NavigationGridBenchmarkTimingSample>(configEntity);
            entityManager.AddBuffer<NavigationGridMovementBenchmarkTimingSample>(configEntity);
            entityManager.AddBuffer<NavigationGridMovementBenchmarkStateTrace>(configEntity);
            entityManager.AddBuffer<NavigationGridMovementBenchmarkAgentTrace>(configEntity);
            entityManager.AddBuffer<NavigationGridBenchmarkStageTimingSample>(configEntity);
            entityManager.AddBuffer<NavigationGridScaleInputMember>(configEntity);
            entityManager.AddBuffer<NavigationGridMovementBenchmarkAgent>(configEntity);
            entityManager.SetComponentData(configEntity, new NavigationGridBenchmarkConfig
            {
                Workload = workload,
                AgentCount = _agentCount,
                RandomSeed = _replayScript.RandomSeed,
                WarmupTicks = math.max(0, _warmupTicks),
                SampleTicks = math.max(1, _sampleTicks),
                SpawnColumnCount = math.max(1, _spawnColumnCount),
                SpawnSpacing = math.max(0.1f, _spawnSpacing),
                SpawnOrigin = _spawnOrigin,
                AgentRadius = 0.35f,
                // 诊断开关只影响 Grid 轨迹，不改变 Legacy 基线配置
                RecordMovementTrace = (byte)(IsMovementTraceRequested() ? 1 : 0),
                GitCommit = new FixedString64Bytes(GetGitCommit()),
                MapSceneHash = new FixedString128Bytes(_mapSceneHash),
                ReplayScriptHash = new FixedString128Bytes(_replayScript.ComputeHash()),
                AutoQuit = (byte)(_autoQuitInBatchMode ? 1 : 0),
            });
            entityManager.SetComponentData(
                configEntity,
                new NavigationGridMovementBenchmarkState());
            DynamicBuffer<NavigationGridBenchmarkCommand> commands =
                entityManager.GetBuffer<NavigationGridBenchmarkCommand>(configEntity);
            // 两种后端处理同一组 Tick 命令，差异只来自寻路实现
            for (int index = 0; index < _replayScript.Commands.Count; index++)
            {
                LegacyNavigationBenchmarkCommandDefinition command = _replayScript.Commands[index];
                commands.Add(new NavigationGridBenchmarkCommand
                {
                    Tick = command.Tick,
                    TargetOffset = command.TargetOffset,
                });
            }

            Debug.Log(
                $"[NavigationBenchmark] Grid workload={workload}，Ani={_agentCount}");
            return true;
        }

        private static NavigationGridBenchmarkWorkload GetGridWorkload()
        {
            const string argumentName = "-grid-benchmark-workload";
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++)
            {
                string argument = arguments[index];
                string value = null;
                if (argument.StartsWith(argumentName + "=", StringComparison.OrdinalIgnoreCase))
                {
                    value = argument[(argumentName.Length + 1)..];
                }
                else if (string.Equals(argument, argumentName, StringComparison.OrdinalIgnoreCase) &&
                         index + 1 < arguments.Length)
                {
                    value = arguments[index + 1];
                }

                if (value != null)
                {
                    if (NavigationGridBenchmarkScaleProfile.TryParseWorkload(
                            value,
                            out NavigationGridBenchmarkWorkload workload))
                    {
                        return workload;
                    }

                    throw new ArgumentException(
                        $"无法识别 Grid Benchmark workload“{value}”，可用值为 " +
                        "path、strict、scaleinput、free、avoidance 或 collision");
                }
            }

            return NavigationGridBenchmarkWorkload.StrictFormationBaseline;
        }

        private static string GetGitCommit()
        {
            const string ArgumentPrefix = "-benchmark-git-commit=";
            string[] arguments = Environment.GetCommandLineArgs();
            // 构建后的 Player 无法可靠读取仓库状态，因此由批处理直接传入提交号
            for (int i = 0; i < arguments.Length; i++)
            {
                if (arguments[i].StartsWith(ArgumentPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[i][ArgumentPrefix.Length..];
                }
            }

            return "working-tree";
        }

        private static bool IsMovementTraceRequested()
        {
            // 使用启动参数控制诊断，避免把调试状态写入共享场景资产
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++)
            {
                if (string.Equals(
                        arguments[index],
                        "-grid-benchmark-trace",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
