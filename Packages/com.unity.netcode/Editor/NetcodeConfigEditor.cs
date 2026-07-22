using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.NetCode.Hybrid;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// 管理全局 <see cref="NetCodeConfig"/> ScriptableObject 创建与登记的 Editor 脚本
    /// </summary>
    [CustomEditor(typeof(NetCodeConfig), true, isFallback = false)]
    internal class NetcodeConfigEditor : UnityEditor.Editor
    {
        private const string k_LiveEditingWarning = " Therefore, be aware that the Global config is applied project-wide automatically:\n - In the Editor; this config is set every frame, enabling live editing. Note that this invalidates (by replacing) any C# code of yours that modifies these NetCode configuration singleton components manually.\n - In a build; this config is applied once (during Server & Client World system creation).";
        private static readonly GUILayoutOption s_ButtonWidth = GUILayout.Width(90);

        private static NetCodeConfig SavedConfig
        {
            get => NetCodeClientAndServerSettings.instance.GlobalNetCodeConfig;
            set
            {
                if (SavedConfig == value) return;
                NetCodeClientAndServerSettings.instance.GlobalNetCodeConfig = value;
                EditorUtility.SetDirty(NetCodeClientAndServerSettings.instance);
                LoadAllNetCodeConfigsAndSetGlobalFlags();
                NetCodeClientAndServerSettings.instance.Save();
            }
        }

        internal static void CreateNetcodeSettingsAsset()
        {
            var assetPath = AssetDatabase.GenerateUniqueAssetPath("Assets/NetcodeConfig.asset");
            var netCodeConfig = CreateInstance<NetCodeConfig>();
            netCodeConfig.IsGlobalConfig = true; // 避免首次创建时出现警告
            AssetDatabase.CreateAsset(netCodeConfig, assetPath);
            Selection.activeObject = SavedConfig = AssetDatabase.LoadAssetAtPath<NetCodeConfig>(assetPath);
        }

        /// <summary>
        /// 修复添加到 Preloaded Assets 的配置不会自动初始化的问题
        /// NetCode 过去使用 Preloaded Assets 保存全局配置，因此用户报告进入 PlayMode 时 NetCodeConfig.Global 无法可靠设置
        /// https://forum.unity.com/threads/occasionally-netcodeconfig-fails-to-load.1535359/
        /// </summary>
        [InitializeOnLoadMethod]
        private static void InitializeNetCodeConfigEditorBugFix()
        {
            if (SavedConfig)
            {
                // 强制加载全局 NetCodeConfig，修复 Editor 中 Resources.Load 的启动问题
                ValidateConfig(SavedConfig);
            }
            else
            {
                // NetCode 1.x 之后移除
                // 部分 NetCode 次要版本曾将该配置保存到 Preloaded Assets
                // 现在已改用自定义 ProjectSettings，不再需要这种方式，但仍需支持自动升级
               // 副作用：若没有全局配置，Editor 首次启动时会加载所有 Preloaded Assets
               var found = PlayerSettings.GetPreloadedAssets().OfType<NetCodeConfig>().FirstOrDefault(x => x.IsGlobalConfig);
               if (found)
               {
                   SavedConfig = found;
                   Debug.LogWarning($"The Global NetCodeConfig ('{found.name}') is now saved into the {nameof(NetCodeClientAndServerSettings)} ProjectAsset! Please ensure you save that file to source control (if applicable). It is now safe to remove this asset from the Preloaded Assets list, if you'd like to. It'll get added automatically during builds. This corrective logic will be removed after Netcode 1.x.");
               }
            }
        }

        /// <summary>
        /// 登记使用 IMGUI 绘制的 Settings Provider
        /// </summary>
        /// <returns></returns>
        [SettingsProvider]
        public static SettingsProvider CreateNetcodeConfigSettingsProvider()
        {
            // 第一个参数是 Settings 窗口中的路径
            // 第二个参数是设置作用域，此处只显示在 Project Settings 窗口
            var provider = new SettingsProvider("Project/Multiplayer", SettingsScope.Project)
            {
                // 未提供 Label 时，默认使用路径的最后一段作为显示名称
                label = "Multiplayer",
                // 创建 SettingsProvider 并就地初始化其 IMGUI 绘制函数
                guiHandler = (searchContext) =>
                {
                    Links();

                    GUILayout.BeginHorizontal();
                    var inst = NetCodeClientAndServerSettings.instance;
                    {
                        EditorGUI.BeginChangeCheck();
                        GUI.enabled = !Application.isPlaying;
                        inst.GlobalNetCodeConfig = EditorGUILayout.ObjectField(new GUIContent(string.Empty, "Select the asset that NetCode will use, by default."), inst.GlobalNetCodeConfig, typeof(NetCodeConfig), allowSceneObjects: false) as NetCodeConfig;

                        if (GUILayout.Button("Find & Set", s_ButtonWidth))
                        {
                            if (SavedConfig == null)
                            {
                                var configs = AssetDatabase.FindAssets($"t:{nameof(NetCodeConfig)}")
                                    .Select(AssetDatabase.GUIDToAssetPath)
                                    .Select(AssetDatabase.LoadAssetAtPath<NetCodeConfig>)
                                    .ToArray();
                                Array.Sort(configs);
                                SavedConfig = configs.FirstOrDefault();
                                EditorGUIUtility.PingObject(SavedConfig);
                            }
                        }

                        if (GUILayout.Button("Create & Set", s_ButtonWidth))
                        {
                            CreateNetcodeSettingsAsset();
                        }

                        if (EditorGUI.EndChangeCheck())
                        {
                            LoadAllNetCodeConfigsAndSetGlobalFlags();
                        }
                    }
                    GUILayout.EndHorizontal();

                    if (!SavedConfig)
                    {
                        EditorGUILayout.HelpBox("No Global NetCodeConfig is set. This is valid, but note that the NetCode package will therefore be configured with default settings, unless otherwise specified (e.g. by modifying the Netcode singleton component values directly in C#).", MessageType.Info);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("You have now set a Global NetCodeConfig asset." + k_LiveEditingWarning, MessageType.Warning);
                    }

                    EditorGUILayout.Separator();

                    // 当前 Importance 建议
                    var prevFlags = inst.hideFlags;
                    inst.hideFlags = HideFlags.None; // 允许编辑
                    var clientAndServerSettingsSO = new SerializedObject(inst, inst);
                    clientAndServerSettingsSO.Update();
                    var CurrentImportanceSuggestionsProperty = clientAndServerSettingsSO.FindProperty(nameof(inst.CurrentImportanceSuggestions));
                    EditorGUILayout.PropertyField(CurrentImportanceSuggestionsProperty);
                    if (clientAndServerSettingsSO.ApplyModifiedProperties())
                    {
                        inst.Save();
                    }
                    inst.hideFlags = prevFlags;
                },

                // 填充搜索关键字以启用智能筛选和 Label 高亮
                keywords = new HashSet<string>(new[] {"NetCode", "NetCodeConfig", "TickRate", "SimulationTickRate", "NetworkTickRate", "NetworkSendRate"}),
            };
            return provider;
        }

        /// <summary>
        /// 该操作开销较高，只在实际设置新配置时执行
        /// </summary>
        private static void LoadAllNetCodeConfigsAndSetGlobalFlags()
        {
            foreach (var config in AssetDatabase.FindAssets($"t:{nameof(NetCodeConfig)}")
                         .Select(AssetDatabase.GUIDToAssetPath)
                         .Select(AssetDatabase.LoadAssetAtPath<NetCodeConfig>))
            {
                if(config)
                    ValidateConfig(config);
            }
        }

        private static void ValidateConfig(NetCodeConfig config)
        {
            var isActuallyGlobalConfig = (config == SavedConfig);
            if (isActuallyGlobalConfig != config.IsGlobalConfig)
            {
                Debug.LogWarning($"Detected individual NetCodeConfig asset ('{AssetDatabase.GetAssetPath(config) ?? config.name}') with incorrect `IsGlobalConfig` flag! Was '{config.IsGlobalConfig}', updated to '{isActuallyGlobalConfig}'. Check for modifications to the {nameof(NetCodeClientAndServerSettings)}.asset, and commit all changed netcode files. These warnings are expected when modifying the Global NetCodeConfig, and are harmless.", config);
                config.IsGlobalConfig = isActuallyGlobalConfig;
                EditorUtility.SetDirty(config);
            }
        }

        private static readonly GUIContent s_ClientServerTickRate = new GUIContent("ClientServerTickRate", "General multiplayer settings.\n\nServer Authoritative - Thus, when a client connects, the server will send an RPC clobbering any existing client values.");
        private static readonly GUIContent s_ClientTickRate = new GUIContent("ClientTickRate", "General multiplayer settings for the client.\n\nCan be configured on a per-client basis (via use of multiple configs, or direct C# component manipulation).");
        private static readonly GUIContent s_GhostSendSystemData = new GUIContent("GhostSendSystemData", "Specific optimization (and debug) settings for the GhostSendSystem to reduce bandwidth and CPU consumption.");
        private static readonly GUIContent s_TransportSettings = new GUIContent("NetworkConfigParameter (Unity Transport)", "Configures various UTP <b>NetworkConfigParameter</b> configuration values, but only when user-code uses one of the built-in <b>INetworkStreamDriverConstructor</b>'s.\n\nTo read this config in your own driver constructors, call <b>DefaultDriverBuilder.AddNetcodePackageDefaultNetworkConfigParameters</b>.");
        private static bool s_TransportSettingsFoldedOut = true;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var config = (NetCodeConfig)target;

            ValidateConfig(config);

            if (config.IsGlobalConfig)
                EditorGUILayout.HelpBox("You have selected this as your Global config." + k_LiveEditingWarning, MessageType.Info);
            if (Application.isPlaying)
                EditorGUILayout.HelpBox("Live tweaking is not supported for disabled values.", MessageType.Warning);

            GUI.enabled = !Application.isPlaying;
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.EnableClientServerBootstrap)));
#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.HostWorldModeSelection)));
#endif
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ClientServerTickRate)), s_ClientServerTickRate);
            GUI.enabled = true;
            ValidateClientServerTickRate(config.ClientServerTickRate);
            GUILayout.Space(15);

            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ClientTickRate)), s_ClientTickRate);
            GUILayout.Space(15);

            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.GhostSendSystemData)), s_GhostSendSystemData);
            ValidateGhostSendSystemData(config.GhostSendSystemData);
            GUILayout.Space(15);

            GUI.enabled = !Application.isPlaying;
            s_TransportSettingsFoldedOut = EditorGUILayout.Foldout(s_TransportSettingsFoldedOut, s_TransportSettings, toggleOnLabelClick: true);
            if (s_TransportSettingsFoldedOut)
            {
                EditorGUI.indentLevel += 2;
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ConnectTimeoutMS)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.MaxConnectAttempts)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.DisconnectTimeoutMS)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.HeartbeatTimeoutMS)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ReconnectionTimeoutMS)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ClientSendQueueCapacity)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ClientReceiveQueueCapacity)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ServerSendQueueCapacity)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.ServerReceiveQueueCapacity)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NetCodeConfig.MaxMessageSize)));
                GUI.enabled = true;
                EditorGUI.indentLevel -= 2;
            }

            GUILayout.Space(15);

            Links();
            serializedObject.ApplyModifiedProperties();
        }

        private static void Links()
        {
            GUILayout.BeginHorizontal();
            {
                if (EditorGUILayout.LinkButton("Manual"))
                    Application.OpenURL("https://docs.unity3d.com/Packages/com.unity.netcode@latest");
                if (EditorGUILayout.LinkButton("RPCs"))
                    Application.OpenURL("https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/rpcs.html");
                if (EditorGUILayout.LinkButton("Input"))
                    Application.OpenURL("https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/command-stream.html");
                if (EditorGUILayout.LinkButton("Snapshot Synchronization"))
                    Application.OpenURL("https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/ghost-snapshots.html");
                if (EditorGUILayout.LinkButton("Client Prediction"))
                    Application.OpenURL("https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/prediction.html");
                if (EditorGUILayout.LinkButton("Optimizations"))
                    Application.OpenURL("https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/optimizations.html");
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(15);
        }

        /// <summary>
        /// 验证 ClientServerTickRate 配置
        /// </summary>
        /// <param name="config">配置副本，避免覆盖原始 ScriptableObject</param>
        private static void ValidateClientServerTickRate(ClientServerTickRate config)
        {
            var previous = config;
            config.ResolveDefaults(); // 在验证前调用，与 NetCode 包运行时行为保持一致

            var s = "Each client will be sent a snapshot on ";
            var networkSendRateInterval = config.CalculateNetworkSendRateInterval();
            var actualEstimatedRate = ((float)config.SimulationTickRate / networkSendRateInterval);
            switch (networkSendRateInterval)
            {
                case 1:
                    s += $"every server tick (i.e. ~{actualEstimatedRate:0} times per second).";
                    break;
                case 2:
                    s += $"every other server tick (i.e. ~{actualEstimatedRate:0.0} times per second), which is approximately a 50% CPU and bandwidth reduction compared to sending every frame.";
                    break;
                case 3:
                    s += $"every third server tick (i.e. ~{actualEstimatedRate:0.0} times per second), which is approximately a 66% CPU and bandwidth reduction compared to sending every frame.";
                    break;
                default:
                    s += $"every {networkSendRateInterval}th server tick (i.e. ~{actualEstimatedRate:0.0} times per second), which is approximately a {100-((int)(100f/networkSendRateInterval))}% CPU and bandwidth reduction compared to sending every frame.";
                    break;
            }
            EditorGUILayout.HelpBox(s, MessageType.Info);
            if (networkSendRateInterval > 1)
            {
                EditorGUILayout.HelpBox($"The server can (and will) now distribute these packet sends across the send interval (i.e. a round-robin approach), distributing the GhostSendSystem CPU cost more evenly across frames, reducing CPU spikes. E.g. If you have 50 clients connected, we'll send ~{Math.Max(1, 50/networkSendRateInterval)} of them a snapshot every tick.", MessageType.Info);
            }

            // 手动处理例外，因为需要验证这些原始字段
            {
                if(previous.SimulationTickRate != 0) config.SimulationTickRate = previous.SimulationTickRate;
                if(previous.NetworkTickRate != 0) config.NetworkTickRate = previous.NetworkTickRate;
            }
            // 执行验证
            {
                FixedList4096Bytes<FixedString64Bytes> errors = default;
                config.ValidateAll(ref errors);
                foreach (var error in errors)
                {
                    EditorGUILayout.HelpBox($"{error}!", MessageType.Error);
                }
            }
        }

        /// <summary>
        /// 验证 GhostSendSystemData 配置
        /// </summary>
        /// <param name="config">配置副本，避免覆盖原始 ScriptableObject</param>
        private void ValidateGhostSendSystemData(GhostSendSystemData config)
        {
            if (config.EnablePerComponentProfiling) EditorGUILayout.HelpBox("You've enabled EnablePerComponentProfiling, which will adversely impact performance.", MessageType.Warning);
            if (config.ForcePreSerialize) EditorGUILayout.HelpBox("You've enabled ForcePreSerialize (a debug setting), which may adversely impact performance.", MessageType.Warning);
            if (config.ForceSingleBaseline) EditorGUILayout.HelpBox("You've enabled ForceSingleBaseline, which will adversely impact bandwidth (often significantly), but improve CPU performance.", MessageType.Warning);
        }


        /// <summary>
        /// 使用与 Localization 包相同的逻辑将全局配置加入构建
        /// 参考 com.unity.localization/Editor/Asset Pipeline/LocalizationBuildPlayer.cs
        /// </summary>
        internal class NetcodeConfigEditorBuildProcess : IPreprocessBuildWithReport, IPostprocessBuildWithReport
        {
            bool m_RemoveFromPreloadedAssets;
            public int callbackOrder => 0;

            /// <summary>
            /// 基本原样复制自 com.unity.localization/Editor/Asset Pipeline/LocalizationBuildPlayer.cs
            /// </summary>
            public void OnPreprocessBuild(BuildReport report)
            {
                m_RemoveFromPreloadedAssets = false;
                if (SavedConfig == null)
                    return;

                // 将 NetCode 设置加入 Preloaded Assets
                var preloadedAssets = PlayerSettings.GetPreloadedAssets();
                bool wasDirty = IsPlayerSettingsDirty();

                if (!preloadedAssets.Contains(SavedConfig))
                {
                    ArrayUtility.Add(ref preloadedAssets, SavedConfig);
                    PlayerSettings.SetPreloadedAssets(preloadedAssets);

                    // 若构建前加入了设置，构建后也应将其移除
                    m_RemoveFromPreloadedAssets = true;

                    // 清除 Dirty 标记，避免写回修改后的文件，参见 Case 1254502
                    if (!wasDirty)
                        ClearPlayerSettingsDirtyFlag();
                }
            }

            /// <summary>
            /// 基本原样复制自 com.unity.localization/Editor/Asset Pipeline/LocalizationBuildPlayer.cs
            /// </summary>
            public void OnPostprocessBuild(BuildReport report)
            {
                if (SavedConfig == null || !m_RemoveFromPreloadedAssets)
                    return;

                bool wasDirty = IsPlayerSettingsDirty();

                var preloadedAssets = PlayerSettings.GetPreloadedAssets();
                ArrayUtility.Remove(ref preloadedAssets, SavedConfig);
                PlayerSettings.SetPreloadedAssets(preloadedAssets);

                // 清除 Dirty 标记，避免写回修改后的文件，参见 Case 1254502
                if (!wasDirty)
                    ClearPlayerSettingsDirtyFlag();
            }

            /// <summary>
            /// 基本原样复制自 com.unity.localization/Editor/Asset Pipeline/LocalizationBuildPlayer.cs
            /// </summary>
            static bool IsPlayerSettingsDirty()
            {
                var settings = Resources.FindObjectsOfTypeAll<PlayerSettings>();
                if (settings != null && settings.Length > 0)
                    return EditorUtility.IsDirty(settings[0]);
                return false;
            }

            /// <summary>
            /// 基本原样复制自 com.unity.localization/Editor/Asset Pipeline/LocalizationBuildPlayer.cs
            /// </summary>
            static void ClearPlayerSettingsDirtyFlag()
            {
                var settings = Resources.FindObjectsOfTypeAll<PlayerSettings>();
                if (settings != null && settings.Length > 0)
                    EditorUtility.ClearDirty(settings[0]);
            }
        }
    }
}
