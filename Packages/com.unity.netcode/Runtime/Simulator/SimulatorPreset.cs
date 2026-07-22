using System;
using System.Collections.Generic;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    /// <summary>
    /// com.unity.transport 模拟器的 Preset
    /// 允许开发者模拟多种网络条件
    /// </summary>
    /// <seealso cref="AppendBaseSimulatorPresets"/>
    /// <seealso cref="AppendAdditionalMobileSimulatorProfiles"/>
    [Serializable]
    public struct SimulatorPreset
    {
        /// <summary>
        /// 用户可以直接修改模拟器 Preset 值，此 Preset 称为 Custom
        /// </summary>
        internal const string k_CustomProfileKey = "Custom / User Defined";
        const string k_CustomProfileTooltip = "Custom indicates that you have modified individual simulator values yourself.";
        const string k_PoorMobileTooltip = "Extremely poor connection quality, completely unsuitable for synchronous multiplayer gaming due to exceptionally high latency. Turn based games <i>may</i> work.";
        const string k_DecentMobileTooltip = "Suitable for synchronous multiplayer, but expect connection instability.\n\nExpect to handle players dropping frequently, and dropping their connection entirely. I.e. Ensure you handle reconnections and quickly detect (and display) wifi issues.";
        const string k_MobileWifiDisclaimer = "Interestingly; while broadband is typical for desktop and console platforms, it's <i>also</i> the most common connection used by mobile players.";
        const string k_FiveGDisclaimer = "\n\n<i><b>In many places, expect this to be 'as good as' or 'better than' home broadband.</b></i>";
        const string k_MinSpecDisclaimer = "\n\n<i><b>This is the minimum supported mobile connection for synchronous gameplay. Expect high ping, jitter, stuttering and packet loss.</b></i>";
        const string k_PlayersAsGoodOrBetter = " players will have a connection as good as this or better.";
        const string k_Regional = "connection to a region-specific server (i.e. a server deployed to serve only their region, continent, or locale).";
        const string k_Perfect = "Represents a player on a \"perfect\" " + k_Regional + "\n\nI.e. Only 5% of " + k_PlayersAsGoodOrBetter;
        const string k_Decent = "Represents a player on a \"decent\" " + k_Regional + "\n\nI.e. Only 25% of " + k_PlayersAsGoodOrBetter;
        const string k_Average = "Represents a player on an \"average\" " + k_Regional + "\n\nI.e. Half of all " + k_PlayersAsGoodOrBetter;
        const string k_Poor = "Represents a player on a \"poor\" " + k_Regional + "\n\nWe strongly recommend testing with this connection quality to understand (and mitigate) how some of your users will experience the game.\n\nI.e. 95% of " + k_PlayersAsGoodOrBetter;
        const string k_InternationalDisclaimer = "\n\n\"International\": A game server deployed to a single region and served globally. Generally not suitable for synchronous multiplayer due to latency requirements, although this approach is appropriate for turn-based or asynchronous game servers.\n\n";
        const string k_InternationalDecent = "Represents a \"decent\" connection from a player connecting to a server hosted <b>outside their region</b>." + k_InternationalDisclaimer + "I.e. 25% of " + k_PlayersAsGoodOrBetter;
        const string k_InternationalAverage = "Represents an \"average\" connection from a player connecting to a server hosted <b>outside their region</b>." + k_InternationalDisclaimer + "I.e. Half of all " + k_PlayersAsGoodOrBetter;
        const string k_InternationalPoor = "Represents a \"poor\" connection from a player connecting to a server hosted <b>outside their region</b>." + k_InternationalDisclaimer + "I.e. 95% of " + k_PlayersAsGoodOrBetter;

        /// <summary>
        /// 最常用的 Profile，包括自定义调试项
        /// 最后更新于 2022 年第三季度
        /// </summary>
        /// <param name="list">要追加内容的列表</param>
        public static void AppendBaseSimulatorPresets(List<SimulatorPreset> list)
        {
            list.Add(new SimulatorPreset(k_CustomProfileKey, -1, -1, -1, 0, k_CustomProfileTooltip));
            list.Add(new SimulatorPreset("Custom / No Internet", 1000, 1000, 100, 0,"Simulate the server becoming completely unreachable."));
            list.Add(new SimulatorPreset("Custom / Unplayable Internet", 300, 400, 30, 0, "Simulate barely having a connection at all, to observe what your users will experience when the internet is good enough to connect (sometimes), but not good enough to play.\n\nIt may take multiple attempts for the driver to connect.\n\nWe recommend detecting a \"minimum threshold of playable\", and to exclude (and inform) users when below this threshold."));
            list.Add(new SimulatorPreset("Custom / MitM (Man-in-the-Middle) Packet Corruption", 200, 400, 2, 1, "Simulate a malicious user attempting to catastrophically err your client, or (more likely) the server."));

            BuildProfiles(list, true, "Broadband [WIFI] / ", 1, 1, 1, k_MobileWifiDisclaimer);
        }

        /// <summary>
        /// <para>根据真实数据对移动网络连接类型作出的最佳近似估计
        /// 最后更新于 2022 年第三季度</para>
        /// <para>来源</para>
        /// <para>- 开发者、多人游戏团队、支持团队与客户</para>
        ///     <para>- https://unity.com/products/multiplay</para>
        ///     <para>- https://www.giffgaff.com/blog/h-5g-lte-a-g-e-new-cell-network-alphabet/</para>
        ///     <para>- https://www.4g.co.uk/how-fast-is-4g/</para>
        /// </summary>
        /// <param name="list">要追加内容的列表</param>
        public static void AppendAdditionalMobileSimulatorProfiles(List<SimulatorPreset> list)
        {
            BuildProfiles(list, false, "2G [!] [CDMA & GSM, '00] / ", 200, 20, 5, k_PoorMobileTooltip);
            BuildProfiles(list, false, "2.5G [!] [GPRS, G, '00] / ", 180, 15, 5, k_PoorMobileTooltip);
            BuildProfiles(list, false, "2.75G [!] [Edge, E, '06] / ", 160, 15, 5, k_PoorMobileTooltip);
            BuildProfiles(list, false, "3G [!] [WCDMA & UMTS, '03 ] / ", 120, 10, 5, k_PoorMobileTooltip);
            BuildProfiles(list, true, "3.5G [HSDPA, H, '06] / ", 65, 10, 5, k_DecentMobileTooltip + k_MinSpecDisclaimer);
            BuildProfiles(list, true, "3.75G [HDSDPA+, H+, '11] / ", 50, 10, 5, k_DecentMobileTooltip);
            BuildProfiles(list, true, "4G [4G, LTE, '13] / ", 35, 5, 3, k_DecentMobileTooltip);
            BuildProfiles(list, true, "4.5G [4G+, LTE-A, '16] / ", 25, 5, 3, k_DecentMobileTooltip);
            BuildProfiles(list, true, "5G ['20] / ", 0, 5, 3, k_DecentMobileTooltip + k_FiveGDisclaimer);
        }

        /// <summary>
        /// <para>根据真实数据对 PC 和主机连接类型作出的最佳近似估计
        /// 最后更新于 2022 年第三季度</para>
        /// <para>来源</para>
        /// <para>- 开发者、多人游戏团队、支持团队与客户</para>
        ///     <para>- https://unity.com/products/multiplay</para>
        /// </summary>
        /// <param name="list">要追加内容的列表</param>
        public static void AppendAdditionalPCSimulatorPresets(List<SimulatorPreset> list)
        {
            list.Add(new SimulatorPreset("LAN [Local Area Network]", 1, 1, 1, 0, "Playing on LAN is generally <1ms (i.e. simulator off), but we've included it for convenience."));
        }

        /// <summary>
        /// 为 Profile 构建子 Profile，例如为自定义 Profile 构建四个地区选项
        /// </summary>
        /// <param name="list">要追加内容的列表</param>
        /// <param name="showRegional">连接质量极差，选择地区服务器也无意义且会造成错误印象时设为 false</param>
        /// <param name="name">Profile 名称，需要让子 Profile 出现在子菜单中时包含正斜杠</param>
        /// <param name="packetDelayMs">Profile 会在此基础上继续增加延迟</param>
        /// <param name="packetJitterMs">Profile 会在此基础上继续增加抖动</param>
        /// <param name="packetLossPercent">Profile 会在此基础上继续增加丢包率</param>
        /// <param name="tooltip">Profile 会在此基础上追加提示内容</param>
        public static void BuildProfiles(List<SimulatorPreset> list, bool showRegional, string name, int packetDelayMs, int packetJitterMs, int packetLossPercent, string tooltip)
        {
            if (tooltip != null)
                tooltip += "\n\n";

            if (showRegional)
            {
                list.Add(new SimulatorPreset(name + "Regional [5th Percentile]", packetDelayMs + 9, packetJitterMs + 1, packetLossPercent + 1, 0, tooltip + k_Perfect));
                list.Add(new SimulatorPreset(name + "Regional [25th Percentile]", packetDelayMs + 15, packetJitterMs + 5, packetLossPercent + 1, 0, tooltip + k_Decent));
                list.Add(new SimulatorPreset(name + "Regional [50th Percentile]", packetDelayMs + 65, packetJitterMs + 10, packetLossPercent + 2, 0, tooltip + k_Average));
                list.Add(new SimulatorPreset(name + "Regional [95th Percentile]", packetDelayMs + 150, packetJitterMs + 10, packetLossPercent + 3, 0, tooltip + k_Poor));
            }

            list.Add(new SimulatorPreset(name + "International [25th Percentile]", packetDelayMs + 60, packetJitterMs + 5, packetLossPercent + 2, 0, tooltip + k_InternationalDecent));
            list.Add(new SimulatorPreset(name + "International [50th Percentile]", packetDelayMs + 120, packetJitterMs + 10, packetLossPercent + 2, 0, tooltip + k_InternationalAverage));
            list.Add(new SimulatorPreset(name + "International [95th Percentile]", packetDelayMs + 200, packetJitterMs + 15, packetLossPercent + 5, 0, tooltip + k_InternationalPoor));
        }

#if UNITY_EDITOR
        /// <summary>
        /// 返回适合目标版本的 Preset
        /// </summary>
        /// <param name="presetGroupName"></param>
        /// <param name="appendPresets"></param>
        public static void DefaultInUseSimulatorPresets(out string presetGroupName, List<SimulatorPreset> appendPresets)
        {
            appendPresets.Add(new SimulatorPreset(k_CustomProfileKey, -1, -1, -1, 0, k_CustomProfileTooltip));
            if (MultiplayerPlayModePreferences.ShowAllSimulatorPresets)
            {
                presetGroupName = "All Presets";
                AppendBaseSimulatorPresets(appendPresets);
                AppendAdditionalPCSimulatorPresets(appendPresets);
                AppendAdditionalMobileSimulatorProfiles(appendPresets);
            }
            else
            {
#if UNITY_IOS || UNITY_ANDROID
                presetGroupName = "Mobile Presets";
                AppendBaseSimulatorPresets(appendPresets);
                AppendAdditionalMobileSimulatorProfiles(appendPresets);
#else
                presetGroupName = "PC & Console Presets";
                AppendBaseSimulatorPresets(appendPresets);
                AppendAdditionalPCSimulatorPresets(appendPresets);
#endif
            }
        }
#endif

        /// <summary>
        /// 当前为用户定义的 Preset 时返回 true
        /// </summary>
        public bool IsCustom => string.IsNullOrWhiteSpace(Name) || Name == k_CustomProfileKey;

        /// <summary>
        /// Preset 名称
        /// 对于用户在 Editor 中修改模拟器设置后形成的自定义 Preset，可以为空
        /// </summary>
        readonly internal string Name;
        /// <summary>
        /// 在模拟器窗口中显示的 Tooltip
        /// </summary>
        readonly internal string Tooltip;
        /// <inheritdoc cref="Unity.Networking.Transport.Utilities.SimulatorUtility.Parameters.PacketDelayMs"/>
        internal int PacketDelayMs;
        /// <inheritdoc cref="Unity.Networking.Transport.Utilities.SimulatorUtility.Parameters.PacketJitterMs"/>
        internal int PacketJitterMs;
        /// <inheritdoc cref="Unity.Networking.Transport.Utilities.SimulatorUtility.Parameters.PacketDropPercentage"/>
        internal int PacketLossPercent;
        /// <inheritdoc cref="SimulatorUtility.Parameters.FuzzFactor"/>
        internal int PacketFuzzPercent;

        // TODO：在后续提交中使用带宽数据

        /// <summary>
        /// 从 <paramref name="allProfiles"/> 列表中获取具有指定 <paramref name="name"/> 的 Preset
        /// </summary>
        /// <param name="name"></param>
        /// <param name="allProfiles"></param>
        /// <param name="preset">名称匹配的 Preset，未找到时为 null</param>
        /// <param name="index">Preset 在列表中的索引，未找到时为 -1</param>
        /// <returns>找到 Preset 时为 true</returns>
        internal static bool TryGetPresetFromName(string name, List<SimulatorPreset> allProfiles, out SimulatorPreset preset, out int index)
        {
            for (var i = 0; i < allProfiles.Count; i++)
            {
                preset = allProfiles[i];
                if (preset.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    return true;
                }
            }
            index = -1;
            preset = default;
            return false;
        }

        /// <summary>
        /// 构造新的 Preset
        /// </summary>
        /// <param name="name">Simulator 名称</param>
        /// <param name="packetDelayMs">数据包延迟，单位为毫秒</param>
        /// <param name="packetJitterMs">数据包抖动，单位为毫秒</param>
        /// <param name="packetLossPercent">数据包丢失百分比</param>
        /// <param name="packetFuzzPercent">数据包模糊处理百分比</param>
        /// <param name="tooltip">Tooltip 字符串</param>
        public SimulatorPreset(string name, int packetDelayMs, int packetJitterMs, int packetLossPercent, int packetFuzzPercent, string tooltip)
        {
            Name = name;
            Tooltip = tooltip;
            PacketDelayMs = packetDelayMs;
            PacketJitterMs = packetJitterMs;
            PacketLossPercent = packetLossPercent;
            PacketFuzzPercent = packetFuzzPercent;
        }

        /// <summary>
        /// 构造新的 Preset
        /// </summary>
        /// <param name="name">Simulator 名称</param>
        /// <param name="packetDelayMs">数据包延迟，单位为毫秒</param>
        /// <param name="packetJitterMs">数据包抖动，单位为毫秒</param>
        /// <param name="packetLossPercent">数据包丢失百分比</param>
        /// <param name="tooltip">Tooltip 字符串</param>
        [Obsolete("Use other constructor. (RemovedAfter 2.0)")]
        public SimulatorPreset(string name, int packetDelayMs, int packetJitterMs, int packetLossPercent, string tooltip)
            : this(name, packetDelayMs, packetJitterMs, packetLossPercent, 0, tooltip)
        {
        }
    }
}
