using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Unity.NetCode.GeneratorTests
{
    class BaseTest
    {
        string? m_OriginalDirectory;
        protected Regex? ErrorLogExclusion;

        [SetUp]
        public void SetupCommon()
        {
            Generators.Debug.LastErrorLog = "";
            m_OriginalDirectory = Environment.CurrentDirectory;
            // 向上查找 com.unity.netcode 目录
            string? currentDir = m_OriginalDirectory;
            while (currentDir?.Length > 0 && !currentDir.EndsWith("com.unity.netcode", StringComparison.Ordinal))
                currentDir = Path.GetDirectoryName(currentDir);

            if (currentDir == null || !currentDir.EndsWith("com.unity.netcode", StringComparison.Ordinal))
            {
                Assert.Fail("Cannot find com.unity.netcode folder");
                return;
            }

            // 在 Runtime/SourceGenerators/Source~ 目录中执行测试
            Environment.CurrentDirectory = Path.Combine(currentDir, "Runtime", "SourceGenerators", "Source~");
            Generators.Profiler.Initialize();
        }

        private bool ErrorLogMatchesExclusion()
        {
            return ErrorLogExclusion != null && ErrorLogExclusion.Matches(Generators.Debug.LastErrorLog).Count > 0;
        }

        [TearDown]
        public void TearDownCommon()
        {
            Environment.CurrentDirectory = m_OriginalDirectory ?? string.Empty;
            if (Generators.Debug.LastErrorLog.Length > 0 && !ErrorLogMatchesExclusion())
            {
                // 部分代码会绕过诊断系统直接写日志，因此这里不能只检查 Diagnostic
                Assert.Fail("Unexpected error log: "+Generators.Debug.LastErrorLog);
            }

            ErrorLogExclusion = null;
        }
    }
}
