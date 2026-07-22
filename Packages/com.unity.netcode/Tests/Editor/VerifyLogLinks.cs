using System.Collections.Generic;
using NUnit.Framework;
using Unity.NetCode.Editor;

namespace Unity.NetCode.Tests
{
    internal class VerifyLogLinks
    {
        // 验证调用 OpenPlayModeTools 日志链接后能按预期打开窗口
        [Test]
        public void VerifyOpenPlayModeTools()
        {
            bool openBeforeTest = false;
            if (UnityEditor.EditorWindow.HasOpenInstances<MultiplayerPlayModeWindow>())
            {
                openBeforeTest = true;
                UnityEditor.EditorWindow.GetWindow<MultiplayerPlayModeWindow>().Close();
            }

            // 调用超链接处理方法
            var args = new Dictionary<string,string>{{"href",NetCodeHyperLinkArguments.s_OpenPlayModeTools.ToString()}};
            MultiplayerPlayModeWindow.HandleHyperLinkArgs( args );

            // 验证窗口已立即打开
            Assert.True(UnityEditor.EditorWindow.HasOpenInstances<MultiplayerPlayModeWindow>());

            if (!openBeforeTest)
            {
                UnityEditor.EditorWindow.GetWindow<MultiplayerPlayModeWindow>().Close();
            }
        }
    }
}
