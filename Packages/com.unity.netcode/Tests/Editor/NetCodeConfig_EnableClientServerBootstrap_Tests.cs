using System;
using System.Collections;
using NUnit.Framework;
using Unity.Entities;
using Unity.NetCode;
using Unity.NetCode.Hybrid;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    [TestFixture]
    [Serializable]
    internal class NetCodeConfig_EnableClientServerBootstrap_Tests
    {
        public NetCodeConfig m_OldNetCodeConfigGlobal;
        public bool m_OldEnterPlayModeOptionsEnabled;
        public EnterPlayModeOptions m_OldEnterPlayModeOptions;
        public NetCodeConfig m_TempNetCodeConfig;
        public string m_TempNetCodeConfigGlobalProjectAsset;
        public bool m_ExpectDomainReload;
        public bool m_WarnWhenTicksBatch;

        [UnityTest, Timeout(60_000), Description("Tests all permutations of the NetCodeConfig.EnableClientServerBootstrap option against all permutations of the EnterPlayModeOptions option. As a secondary test: It ensures no runtime errors when domain reloads is either on or off."),]
        [Ignore("Unstable test that times out and not properly cleaning NetCodeConfig upon such event which leads to more failures. Tracked in MTT-13034")]
        public IEnumerator Test([Values] NetCodeConfig.AutomaticBootstrapSetting value,
            [Values(EnterPlayModeOptions.None,
                EnterPlayModeOptions.DisableDomainReload,
                EnterPlayModeOptions.DisableSceneReload,
                EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload)]
            EnterPlayModeOptions enterPlayModeOptions)
        {
            // 初始化测试环境
            LogAssert.ignoreFailingMessages = false;
            m_OldNetCodeConfigGlobal = NetCodeClientAndServerSettings.instance.GlobalNetCodeConfig;
            m_OldEnterPlayModeOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            m_OldEnterPlayModeOptions = EditorSettings.enterPlayModeOptions;
            m_TempNetCodeConfig = ScriptableObject.CreateInstance<NetCodeConfig>();
            m_TempNetCodeConfig.IsGlobalConfig = true;
            m_TempNetCodeConfigGlobalProjectAsset = AssetDatabase.GenerateUniqueAssetPath("Assets/NetCodeConfigGlobal_Test.asset");
#if UNITY_EDITOR
            m_WarnWhenTicksBatch = MultiplayerPlayModePreferences.WarnBatchedTicks;
            MultiplayerPlayModePreferences.WarnBatchedTicks = false;
#endif
            NetCodeConfig.Global = m_TempNetCodeConfig;
            AssetDatabase.CreateAsset(NetCodeConfig.Global, m_TempNetCodeConfigGlobalProjectAsset);
            NetCodeClientAndServerSettings.instance.GlobalNetCodeConfig = NetCodeConfig.Global;

            // 执行 PlayMode 测试
            EditorSettings.enterPlayModeOptionsEnabled = enterPlayModeOptions != EnterPlayModeOptions.None;
            EditorSettings.enterPlayModeOptions = enterPlayModeOptions;
            NetCodeClientAndServerSettings.instance.GlobalNetCodeConfig.EnableClientServerBootstrap = value;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            m_ExpectDomainReload = (enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;
            yield return new EnterPlayMode(m_ExpectDomainReload); // 该步骤触发 Domain Reload 时会清空全部私有或静态字段数据
                                                                  // 方法参数除外
            var hasClientWorlds = ClientServerBootstrap.HasClientWorlds;
            var hasServerWorld = ClientServerBootstrap.HasServerWorld;

            // 退出 PlayMode 并恢复编辑器配置
            yield return new ExitPlayMode();
            World.DisposeAllWorlds();
            EditorSettings.enterPlayModeOptions = m_OldEnterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = m_OldEnterPlayModeOptionsEnabled;
            NetCodeClientAndServerSettings.instance.GlobalNetCodeConfig = m_OldNetCodeConfigGlobal;
            AssetDatabase.DeleteAsset(m_TempNetCodeConfigGlobalProjectAsset);
#if UNITY_EDITOR
            MultiplayerPlayModePreferences.WarnBatchedTicks = m_WarnWhenTicksBatch;
#endif
            // 验证自动 Bootstrap 是否按配置创建客户端和服务端 World
            var expectNetCodeWorlds = value == NetCodeConfig.AutomaticBootstrapSetting.EnableAutomaticBootstrap;
            Assert.AreEqual(expectNetCodeWorlds, hasClientWorlds, nameof(hasClientWorlds));
            Assert.AreEqual(expectNetCodeWorlds, hasServerWorld, nameof(hasServerWorld));
            LogAssert.NoUnexpectedReceived();
        }
    }
}
