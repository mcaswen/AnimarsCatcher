using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Scenes;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// 通过设置 <see cref="NumThinClientsRequested"/>，让 NetCode 自动管理 Thin Client
    /// </summary>
    public class AutomaticThinClientWorldsUtility
    {
        /// <summary>
        /// 设置所需的 Thin Client World 数量
        /// </summary>
        /// <remarks>
        /// 如果为默认值 null，则在编辑器中使用 <see cref="MultiplayerPlayModePreferences.RequestedNumThinClients"/>，其他环境使用 0
        /// 在构建版本中，只有接入 <see cref="UpdateAutomaticThinClientWorlds"/> 后才会创建 World
        /// </remarks>
        public static int? NumThinClientsRequested;

        /// <summary>
        /// 创建 Thin Client World 的频率，单位为 Hz，即每秒创建的 World 数量
        /// 0 表示立即创建全部 World
        /// 如果为默认值 null，则在编辑器中使用 <see cref="MultiplayerPlayModePreferences.ThinClientCreationFrequency"/>，其他环境使用 0
        /// </summary>
        public static float? CreationFrequency;

        /// <summary>
        /// 用于注入数据的 World，例如确定要加载哪些 SubScene
        /// 如果为 null，则尝试使用通过 <see cref="ClientServerBootstrap.ClientWorld"/> 等入口找到的现有客户端或服务器 World
        /// </summary>
        public static World ReferenceWorld;

        /// <summary>
        ///     如果自动 Thin Client 在 Bootstrap 期间需要自定义初始化，例如使用了自定义场景管理设置，请修改此委托
        ///     默认使用 <see cref="DefaultBootstrapThinClientWorldInitialization"/>
        ///     设为 null 可禁用 Bootstrap 初始化功能
        /// </summary>
        public static ThinClientWorldInitializationDelegate BootstrapInitialization = DefaultBootstrapThinClientWorldInitialization;

        /// <summary>
        ///     如果自动 Thin Client 在运行时需要自定义初始化，例如使用了自定义场景管理设置，请修改此委托
        ///     默认使用 <see cref="DefaultRuntimeThinClientWorldInitialization"/>
        ///     设为 null 可禁用运行时初始化功能
        /// </summary>
        public static ThinClientWorldInitializationDelegate RuntimeInitialization = DefaultRuntimeThinClientWorldInitialization;

        /// <summary>

        /// 表示是否启用 Bootstrap 阶段的 Thin Client 自动创建

        /// </summary>
        public static bool IsBootstrapInitializationEnabled => BootstrapInitialization != null;

        /// <summary>

        /// 表示是否启用运行时 Thin Client 自动创建

        /// </summary>
        public static bool IsRuntimeInitializationEnabled => RuntimeInitialization != null;

        /// <summary>
        /// 由 NetCode 包自身创建并管理的全部 Thin Client World 列表
        /// 如果将 Thin Client 添加到此列表，NetCode 将接管其所有权
        /// 只有此列表中的 Thin Client World 才会被 NetCode 包删除
        /// </summary>
        public static List<World> AutomaticallyManagedWorlds { get; } = new();

        private static double s_LastSpawnRealtime;

        /// <summary><see cref="DefaultBootstrapThinClientWorldInitialization"/> 和
        /// <see cref="DefaultRuntimeThinClientWorldInitialization"/> 使用的委托</summary>
        /// <param name="referenceWorld">创建新 World 时引用的 World，用于场景加载等用途</param>
        /// <returns>新创建的 World，否则返回 null</returns>
        public delegate World ThinClientWorldInitializationDelegate(World referenceWorld);

        /// <summary>
        /// 通过 <see cref="RuntimeInitializeOnLoadMethodAttribute"/> 和
        /// <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/> 将此工具重置为初始值
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            NumThinClientsRequested = default;
            CreationFrequency = default;
            s_LastSpawnRealtime = default;
            ReferenceWorld = default;
            BootstrapInitialization = DefaultBootstrapThinClientWorldInitialization;
            RuntimeInitialization = DefaultRuntimeThinClientWorldInitialization;
            CleanupWorlds();
        }

        /// <summary>

        /// 从列表中移除所有失效 World 的工具方法

        /// </summary>
        /// <returns>移除的数量</returns>
        public static int CleanupWorlds() => AutomaticallyManagedWorlds.RemoveAll(x => x == null || !x.IsCreated);

        /// <summary>
        /// 默认情况下，Bootstrap 期间创建的 Thin Client 会自动注入已加载场景的 SubScene
        /// 因此无需执行任何自定义处理
        /// </summary>
        /// <param name="referenceWorld">创建新 World 时引用的 World，用于场景加载等用途</param>
        /// <returns>新创建的 World，否则返回 null</returns>
        public static World DefaultBootstrapThinClientWorldInitialization(World referenceWorld)
        {
            return ClientServerBootstrap.CreateThinClientWorld();
        }

        /// <inheritdoc cref="RuntimeInitialization"/>
        /// <param name="referenceWorld">创建新 World 时引用的 World，用于场景加载等用途</param>
        /// <returns>新创建的 World，否则返回 null</returns>
        public static World DefaultRuntimeThinClientWorldInitialization(World referenceWorld)
        {
            if (referenceWorld?.IsCreated != true)
            {
                UnityEngine.Debug.LogError($"Cannot properly initialize ThinClientWorld as referenceWorld:{referenceWorld} is null, so no idea which scenes to load.");
                return null;
            }

            var newThinClientWorld = ClientServerBootstrap.CreateThinClientWorld();
            using var serverWorldScenesQuery = referenceWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<RequestSceneLoaded>(), ComponentType.ReadOnly<SceneReference>());
            var serverWorldScenes = serverWorldScenesQuery.ToComponentDataArray<SceneReference>(Allocator.Temp);
            for (int i = 0; i < serverWorldScenes.Length; i++)
            {
                var desiredGoSceneReferenceGuid = serverWorldScenes[i];
                SceneSystem.LoadSceneAsync(newThinClientWorld.Unmanaged,
                    desiredGoSceneReferenceGuid.SceneGUID,
                    new SceneSystem.LoadParameters
                    {
                        Flags = SceneLoadFlags.BlockOnImport | SceneLoadFlags.BlockOnStreamIn,
                        AutoLoad = true,
                    });
            }
            return newThinClientWorld;
        }

        /// <summary>
        /// 在 <see cref="ClientServerBootstrap.Initialize"/> 流程内使用此方法
        /// </summary>
        /// <remarks>
        /// 此方法必须存在，因为 Entities/NetCode 使用一条快速路径：
        /// 一次性加载所有已加载场景的 Entity 场景数据，再自动把这些数据注入所有合适的 Bootstrap World
        /// </remarks>
        public static void BootstrapThinClientWorlds()
        {
            if (!IsBootstrapInitializationEnabled) return;
            var requestedNumThinClients = NumThinClientsRequested ?? 0;
#if UNITY_EDITOR
            if(NumThinClientsRequested == null) requestedNumThinClients = MultiplayerPlayModePreferences.RequestedNumThinClients;
#endif
            for (var i = 0; i < requestedNumThinClients; i++)
            {
                var newThinClientWorld = BootstrapInitialization(ReferenceWorld);
                if (newThinClientWorld != null && newThinClientWorld.IsCreated)
                    AutomaticallyManagedWorlds.Add(newThinClientWorld);
            }

        }

        /// <summary>
        /// 如果使用此功能，请在 <see cref="MonoBehaviour"/> 的 Update 方法中调用此方法
        /// 它会应用当前配置值
        /// </summary>
        /// <returns>如果创建或销毁了任何 World，则返回 true</returns>
        public static bool UpdateAutomaticThinClientWorlds()
        {
            var requestedNumThinClients = NumThinClientsRequested ?? 0;
            var instantiationFrequency = CreationFrequency ?? 0f;
#if UNITY_EDITOR
            if (!UnityEditor.EditorApplication.isPlaying || UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorApplication.isPaused)
                return false;
            // 创建和销毁 Thin Client 的开销较高，因此编辑数值时禁止发生变化
            if (UnityEditor.EditorGUIUtility.editingTextField)
                return false;
            if(NumThinClientsRequested == null) requestedNumThinClients = MultiplayerPlayModePreferences.RequestedNumThinClients;
            if(CreationFrequency == null) instantiationFrequency = MultiplayerPlayModePreferences.ThinClientCreationFrequency;
#endif
            int maxAllowedToSpawn;
            if (instantiationFrequency == 0)
            {
                maxAllowedToSpawn = int.MaxValue;
            }
            else
            {
                maxAllowedToSpawn = 1;
                var elapsedSecondsSinceLastSpawn = Time.realtimeSinceStartupAsDouble - s_LastSpawnRealtime;
                if (elapsedSecondsSinceLastSpawn < 1d / instantiationFrequency)
                    maxAllowedToSpawn = 0;
            }
            UpdateAutomaticThinClientWorldsImmediate(ReferenceWorld, requestedNumThinClients, maxAllowedToSpawn, out var didCreateOrDestroy);
            return didCreateOrDestroy;
        }

        /// <summary>
        /// 创建或销毁 Thin Client World，直到最终数量等于 <see cref="targetThinClientCount"/>
        /// </summary>
        /// <param name="referenceWorld">要用作引用的 World，如果为 null，则尝试使用任意现有客户端或服务器 World</param>
        /// <param name="targetThinClientCount">所需的 Thin Client 最终数量</param>
        /// <param name="maxAllowedSpawn">频率限制，每次立即销毁 World，但只按此频率实例化</param>
        /// <param name="didCreateOrDestroy">如果创建或销毁了 World，则为 true</param>
        /// <returns>成功创建的 World 列表，否则返回默认值</returns>
        public static NativeList<WorldUnmanaged> UpdateAutomaticThinClientWorldsImmediate(World referenceWorld, int targetThinClientCount, int maxAllowedSpawn, out bool didCreateOrDestroy)
        {
            referenceWorld ??= ClientServerBootstrap.ServerWorld ?? ClientServerBootstrap.ClientWorld;
            didCreateOrDestroy = false;

            // 数量过多时销毁
            didCreateOrDestroy |= CleanupWorlds() > 0;
            var autoWorlds = AutomaticallyManagedWorlds;
            while(autoWorlds.Count > targetThinClientCount)
            {
                var index = autoWorlds.Count - 1;
                var world = autoWorlds[index];
                autoWorlds.RemoveAt(index);
                if (world.IsCreated)
                    world.Dispose();
                didCreateOrDestroy = true;
            }

            // 创建新 World
            var maxAllowedToSpawn = math.clamp(targetThinClientCount - autoWorlds.Count, 0, maxAllowedSpawn);
            NativeList<WorldUnmanaged> newWorlds = default;
            var runtimeCreationIsEnabled = RuntimeInitialization != null;
            if (runtimeCreationIsEnabled && referenceWorld != null && referenceWorld.IsCreated)
            {
                newWorlds = new NativeList<WorldUnmanaged>(maxAllowedToSpawn, Allocator.Temp);
                for(var newIdx = 0; newIdx < maxAllowedToSpawn; newIdx++)
                {
                    didCreateOrDestroy = true;
                    var newThinClientWorld = RuntimeInitialization(referenceWorld);
                    if (newThinClientWorld != null && newThinClientWorld.IsCreated)
                    {
                        autoWorlds.Add(newThinClientWorld);
                        newWorlds.Add(newThinClientWorld.Unmanaged);
                    }
                    s_LastSpawnRealtime = Time.realtimeSinceStartupAsDouble;
                }
            }
            return newWorlds;
        }
    }
}
