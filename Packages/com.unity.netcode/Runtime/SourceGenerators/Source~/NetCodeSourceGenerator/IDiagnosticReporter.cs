using System;
using Microsoft.CodeAnalysis;

namespace Unity.NetCode.Generators
{
    /// <summary>
    /// 报告诊断问题的通用接口
    /// </summary>
    internal interface IDiagnosticReporter
    {
        void LogDebug(string message, Location location);
        void LogDebug(string message,
            [System.Runtime.CompilerServices.CallerFilePath]
            string sourceFilePath = "",
            [System.Runtime.CompilerServices.CallerLineNumber]
            int sourceLineNumber = 0);

        void LogInfo(string message, Location location);
        void LogInfo(string message,
            [System.Runtime.CompilerServices.CallerFilePath]
            string sourceFilePath = "",
            [System.Runtime.CompilerServices.CallerLineNumber]
            int sourceLineNumber = 0);

        void LogWarning(string message, Location location);
        void LogWarning(string message,
            [System.Runtime.CompilerServices.CallerFilePath]
            string sourceFilePath = "",
            [System.Runtime.CompilerServices.CallerLineNumber]
            int sourceLineNumber = 0);

        void LogError(string message, Location location);
        void LogError(string message,
            [System.Runtime.CompilerServices.CallerFilePath]
            string sourceFilePath = "",
            [System.Runtime.CompilerServices.CallerLineNumber]
            int sourceLineNumber = 0);

        void LogException(Exception e, Location location);
        void LogException(Exception e,
            [System.Runtime.CompilerServices.CallerFilePath]
            string sourceFilePath = "",
            [System.Runtime.CompilerServices.CallerLineNumber]
            int sourceLineNumber = 0);
    }
}
