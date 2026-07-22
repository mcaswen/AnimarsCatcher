#if UNITY_EDITOR || NETCODE_DEBUG
using System;
using System.IO;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    /// <summary>
    ///     编辑器中使用 <see cref="MultiplayerPlayModePreferences"/>
    ///     开发构建中可以通过命令行参数加载并启用 JSON 参数
    ///     正式构建中 Network Simulator 始终禁用
    /// </summary>
    public static class NetworkSimulatorSettings
    {
#if UNITY_EDITOR
        /// <summary>
        /// 是否正在使用 UTP Network Simulator Stage，编辑器中的值来自 Multiplayer PlayTools Window
        /// </summary>
        public static bool Enabled => MultiplayerPlayModePreferences.SimulatorEnabled;
        /// <summary>
        /// 模拟使用的参数值，通过 Multiplayer PlayTools Window 设置
        /// </summary>
        public static SimulatorUtility.Parameters ClientSimulatorParameters => MultiplayerPlayModePreferences.ClientSimulatorParameters;
#else
        /// <summary>
        /// 是否正在使用 UTP Network Simulator Stage，可在开发构建中切换
        /// </summary>
        public static bool Enabled { get; private set; }
        /// <summary>
        /// 模拟使用的参数值，可在开发构建中按需设置
        /// </summary>
        public static SimulatorUtility.Parameters ClientSimulatorParameters { get; private set; }
#endif

        static NetworkSimulatorSettings()
        {
#if !UNITY_EDITOR
            CheckCommandLineArgs();
#endif
        }

        /// <summary>
        /// 用于测试真实弱网环境的一组合理默认值
        /// </summary>
        public static SimulatorUtility.Parameters DefaultSimulatorParameters => new SimulatorUtility.Parameters
            {
                Mode = ApplyMode.AllPackets, MaxPacketSize = NetworkParameterConstants.MaxMessageSize, MaxPacketCount = 200,
                FuzzFactor = 0, PacketDelayMs = 100, PacketJitterMs = 10, PacketDropPercentage = 1, PacketDuplicationPercentage = 1
            };

#if !UNITY_EDITOR
        /// <summary>
        ///     检查是否存在 `--loadNetworkSimulatorJsonFile`，如果已设置，则把 <see cref="Enabled"/> 设为 true，
        ///     并写入 <see cref="ClientSimulatorParameters"/>
        ///     如果找不到文件，则记录错误并改用 <see cref="DefaultSimulatorParameters"/>
        ///     也可以使用 `--createNetworkSimulatorJsonFile` 自动生成文件
        /// </summary>
        public static void CheckCommandLineArgs()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                var createTriggered = string.Compare(args[i], "--createNetworkSimulatorJsonFile", StringComparison.OrdinalIgnoreCase) == 0;
                var useTriggered = !createTriggered && string.Compare(args[i], "--loadNetworkSimulatorJsonFile", StringComparison.OrdinalIgnoreCase) == 0;
                if (createTriggered || useTriggered)
                {
                    var simulatorJsonFilePath = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.OrdinalIgnoreCase) ? args[i + 1] : "NetworkSimulatorProfile.json";
                    simulatorJsonFilePath = Path.GetFullPath(simulatorJsonFilePath);

                    if (!simulatorJsonFilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        simulatorJsonFilePath += ".json";

                    var fileInfo = new FileInfo(simulatorJsonFilePath);
                    fileInfo.Refresh();
                    if (!fileInfo.Exists)
                    {
                        if (createTriggered)
                        {
                            UnityEngine.Debug.Log($"Commandline arg '--createNetworkSimulatorJsonFile' passed, but no JSON file found at path '{fileInfo.FullName}'. Creating a 'default' one now using `DefaultSimulatorParameters`.");
                            var json = UnityEngine.JsonUtility.ToJson(DefaultSimulatorParameters, true);
                            File.WriteAllText(fileInfo.FullName, json);
                        }
                        else
                        {
                            UnityEngine.Debug.LogError($"Commandline arg '--loadNetworkSimulatorJsonFile' passed, but no JSON file found at path '{fileInfo.FullName}'. Using `DefaultSimulatorParameters` instead.");
                            Enabled = true;
                            ClientSimulatorParameters = DefaultSimulatorParameters;
                        }
                    }

                    try
                    {
                        var jsonText = File.ReadAllText(fileInfo.FullName);
                        ClientSimulatorParameters = UnityEngine.JsonUtility.FromJson<SimulatorUtility.Parameters>(jsonText);
                        Enabled = true;
                        UnityEngine.Debug.Log($"Enabled network simulator via command line arg '--loadNetworkSimulatorJsonFile' using '{fileInfo.FullName}': {ClientSimulatorParameters.Mode} with {ClientSimulatorParameters.PacketDelayMs}±{ClientSimulatorParameters.PacketJitterMs}ms!");
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogError($"Exception thrown attempting to enable network simulator via command line arg '--loadNetworkSimulatorJsonFile' while applying JSON file '{fileInfo.FullName}'. Exception: '{e}'!");
                    }
                    break;
                }
            }
        }
#endif

        /// <summary>
        ///     遍历 Driver 并使用传入设置更新其 Simulator Pipeline 的工具方法
        /// </summary>
        /// <param name="parameters">要应用到运行中 Driver 的设置</param>
        /// <param name="store">用于获取 Driver 的 Store</param>
        public static void RefreshSimulationPipelineParametersLive(in SimulatorUtility.Parameters parameters, ref NetworkDriverStore store)
        {
            for (var i = store.FirstDriver; i < store.LastDriver; ++i)
            {
                ref var driverInstance = ref store.GetDriverInstanceRW(i);
                if (!driverInstance.simulatorEnabled) continue;

                var driverCurrentSettings = driverInstance.driver.CurrentSettings;
                var simParams = driverCurrentSettings.GetSimulatorStageParameters();
                simParams.Mode = parameters.Mode;
                simParams.PacketDelayMs = parameters.PacketDelayMs;
                simParams.PacketJitterMs = parameters.PacketJitterMs;
                simParams.PacketDropPercentage = 0; // 设为零，避免重复应用丢包
                simParams.PacketDropInterval = parameters.PacketDropInterval;
                simParams.PacketDuplicationPercentage = parameters.PacketDuplicationPercentage;
                simParams.FuzzFactor = parameters.FuzzFactor;
                simParams.FuzzOffset = parameters.FuzzOffset;
                driverInstance.driver.ModifySimulatorStageParameters(simParams);

                // 新 Simulator 的功能较少，但可以丢弃所有数据包，包括底层连接数据包，从而测试超时等场景
                // 因此在这里配置它，而不是配置 Light Simulator
                driverInstance.driver.ModifyNetworkSimulatorParameters(new NetworkSimulatorParameter
                {
                    ReceivePacketLossPercent = parameters.PacketDropPercentage,
                    SendPacketLossPercent = parameters.PacketDropPercentage,
                });
            }
        }

        /// <summary>
        /// 处理 `PacketDropPercentage` 会应用到两个 Pipeline 这一新差异的便捷方法
        /// </summary>
        /// <param name="settings">要修改的设置</param>
        public static void SetSimulatorSettings(ref NetworkSettings settings)
        {
            var parameters = ClientSimulatorParameters;
            // 新 Simulator 的功能较少，但可以丢弃所有数据包，包括底层连接数据包，从而测试超时等场景
            // 因此在这里配置它，而不是配置 Light Simulator
            settings.WithNetworkSimulatorParameters(parameters.PacketDropPercentage, parameters.PacketDropPercentage);

            // 设为零，避免重复应用丢包
            parameters.PacketDropPercentage = 0;
            settings.AddRawParameterStruct(ref parameters);
        }
    }
}
#endif
