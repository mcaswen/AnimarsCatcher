using UnityEditor;
using NUnit.Framework;
using Unity.Entities.Build;
using Unity.NetCode.Tests;

namespace Unity.Scenes.Editor.Tests
{
    internal class DotsGlobalSettingsTests : TestWithSceneAsset
    {
        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void NetCodeDebugDefine_IsSetForDevelopmentBuild()
        {
            var originalValue = EditorUserBuildSettings.development;
            try
            {
                EditorUserBuildSettings.development = true;
                var dotsSettings = DotsGlobalSettings.Instance;
#if NETCODE_NDEBUG // 通过 ProjectSettings 在项目级定义 NETCODE_NDEBUG 时，构建中也应禁用调试功能
                CollectionAssert.DoesNotContain(dotsSettings.ClientProvider.GetExtraScriptingDefines(), "NETCODE_DEBUG");
                CollectionAssert.DoesNotContain(dotsSettings.ServerProvider.GetExtraScriptingDefines(), "NETCODE_DEBUG");
#else
                CollectionAssert.Contains(dotsSettings.ClientProvider.GetExtraScriptingDefines(), "NETCODE_DEBUG");
                CollectionAssert.Contains(dotsSettings.ServerProvider.GetExtraScriptingDefines(), "NETCODE_DEBUG");
#endif
                EditorUserBuildSettings.development = false;
                CollectionAssert.DoesNotContain(dotsSettings.ClientProvider.GetExtraScriptingDefines(),
                    "NETCODE_DEBUG");
                CollectionAssert.DoesNotContain(dotsSettings.ServerProvider.GetExtraScriptingDefines(),
                    "NETCODE_DEBUG");
            }
            finally
            {
                EditorUserBuildSettings.development = originalValue;
            }
        }
    }
}
