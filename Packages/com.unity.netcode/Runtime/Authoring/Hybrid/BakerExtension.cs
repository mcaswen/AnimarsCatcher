using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Hybrid
{
    /// <summary>
    /// 用于构建客户端和服务器目标的构建设置接口
    /// </summary>
    internal interface INetCodeConversionTarget
    {
        NetcodeConversionTarget NetcodeTarget { get; }
    }

    /// <summary>
    /// NetCode 在烘焙过程中使用的 <see cref="Baker{TAuthoringType}"/> 扩展工具方法集合
    /// </summary>
    public static class BakerExtensions
    {
        /// <summary>
        /// 当前烘焙所使用的转换目标
        /// </summary>
        /// <param name="self">Baker 实例</param>
        /// <param name="isPrefab">是否正在转换 Prefab</param>
        /// <typeparam name="T">Baker 类型</typeparam>
        /// <remarks><para>在编辑器中，如果用于转换的构建配置包含 <see cref="NetCodeConversionSettings"/>，
        /// 则使用构建组件指定的目标</para>
        /// <para>
        /// 否则，运行时转换会根据目标 World 确定转换目标
        /// 如果没有适用设置或正在处理 Prefab，则始终回退为 <see cref="NetcodeConversionTarget.ClientAndServer"/>
        /// </para>
        /// </remarks>
        /// <returns>烘焙所使用的转换目标</returns>
        public static NetcodeConversionTarget GetNetcodeTarget<T>(this Baker<T> self, bool isPrefab) where T : Component
        {
            // 使用构建设置检测目标，该逻辑供 SubScene 使用
#if UNITY_EDITOR
#if USING_PLATFORMS_PACKAGE
            if (self.TryGetBuildConfigurationComponent<NetCodeConversionSettings>(out var settings))
            {
                // Debug.LogWarning("构建设置转换目标：" + settings.Target);
                return settings.Target;
            }
#endif

            var settingAsset = self.GetDotsSettings();
            if (settingAsset is INetCodeConversionTarget asset)
            {
                return asset.NetcodeTarget;
            }
#endif

            // 使用实体转换时，Prefab 始终同时转换为客户端和服务器版本，因为它们需要共享同一个 Blob Asset
            if (!isPrefab)
            {
                if (self.IsClient())
                    return NetcodeConversionTarget.Client;
                if (self.IsServer())
                    return NetcodeConversionTarget.Server;
            }

            return NetcodeConversionTarget.ClientAndServer;
        }
    }
}
