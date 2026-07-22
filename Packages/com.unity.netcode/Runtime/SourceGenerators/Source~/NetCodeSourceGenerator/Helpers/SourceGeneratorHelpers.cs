using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Unity.NetCode.Generators
{
    /// <summary>
    /// 用于调试 Source Generator 的辅助能力，主要包括：
    /// - 写入文件日志与报告编译器诊断
    /// </summary>
    internal static class Helpers
    {
        static private ThreadLocal<string> s_OutputFolder;
        static private ThreadLocal<string> s_ProjectPath;
        static private ThreadLocal<bool> s_IsUnity2021_OrNewer;
        static private ThreadLocal<bool> s_SupportTemplatesFromAdditionalFiles;
        static private ThreadLocal<bool> s_WriteLogToDisk;
        static private ThreadLocal<bool> s_CanWriteFiles;
        static private ThreadLocal<LoggingLevel> s_LogLevel;

        public enum LoggingLevel : int
        {
            Debug = 0,
            Info = 1,
            Warning = 2,
            Error = 3,
        }

        static public string ProjectPath
        {
            get => s_ProjectPath.Value;
            private set => s_ProjectPath.Value = value;
        }
        static public string OutputFolder
        {
            get => s_OutputFolder.Value;
            private set => s_OutputFolder.Value = value;
        }

        static public bool WriteLogToDisk
        {
            get => s_WriteLogToDisk.Value;
            private set => s_WriteLogToDisk.Value = value;
        }

        static public bool CanWriteFiles
        {
            get => s_CanWriteFiles.Value;
            private set => s_CanWriteFiles.Value = value;
        }

        static public LoggingLevel CurrentLogLevel => s_LogLevel.Value;

        static Helpers()
        {
            s_OutputFolder = new ThreadLocal<string>(()=> Path.Combine("Temp", "NetCodeGenerated"));
            s_ProjectPath = new ThreadLocal<string>();
            s_IsUnity2021_OrNewer = new ThreadLocal<bool>();
            s_SupportTemplatesFromAdditionalFiles = new ThreadLocal<bool>();
            s_WriteLogToDisk = new ThreadLocal<bool>();
            s_CanWriteFiles = new ThreadLocal<bool>();
            s_LogLevel = new ThreadLocal<LoggingLevel>();
        }

        static public void SetupContext(GeneratorExecutionContext executionContext)
        {
            ProjectPath = null;
            // 默认允许将生成文件和日志写入磁盘，可通过 GlobalOptions 修改该行为
            CanWriteFiles = true;
            WriteLogToDisk = true;
            if (executionContext.AdditionalFiles.Any() && !string.IsNullOrEmpty(executionContext.AdditionalFiles[0].Path))
                ProjectPath = executionContext.AdditionalFiles[0].GetText()?.ToString();
            // 解析全局选项并覆盖默认行为，测试与 Unity 2021 及更高版本 Editor 都会使用这些选项
            ProjectPath = executionContext.GetOptionsString(GlobalOptions.ProjectPath, ProjectPath);
            OutputFolder = executionContext.GetOptionsString(GlobalOptions.OutputPath, OutputFolder);

            // 项目路径无效时无法将文件或日志写入磁盘
            if (string.IsNullOrEmpty(ProjectPath))
            {
                WriteLogToDisk = false;
                CanWriteFiles = false;
                Debug.LogWarning("Unable to setup/find the project path. Forcibly disable writing logs and files to disk");
            }
            else
            {
                Directory.CreateDirectory(GetOutputPath());
                CanWriteFiles = executionContext.GetOptionsFlag(GlobalOptions.WriteFilesToDisk, CanWriteFiles);
                WriteLogToDisk = executionContext.GetOptionsFlag(GlobalOptions.WriteLogsToDisk, WriteLogToDisk);
            }

            // 默认日志级别为 Info，用户可通过调试配置修改；当前 Info 输出量很小
            s_LogLevel.Value = LoggingLevel.Info;
            var loggingLevel = executionContext.GetOptionsString(GlobalOptions.LoggingLevel);
            if (!string.IsNullOrEmpty(loggingLevel) && Enum.TryParse<LoggingLevel>(loggingLevel.ToLower(), out var logLevel))
                s_LogLevel.Value = logLevel;
        }

        public static string GetOutputPath()
        {
            return Path.Combine(ProjectPath, OutputFolder);
        }

        // Unity 2020.x 需要从 Package 与其他目录解析 Template，因此需要该路径解析逻辑
        private static string FindProjectFolderFromAdditionalFile(string folder)
        {
            var index = folder.LastIndexOf("/Library/", StringComparison.Ordinal);
            if(index < 0)
                index = folder.LastIndexOf("\\Library\\", StringComparison.Ordinal);
            return index > 0 ? folder.Substring(0, index) : null;
        }

        public static ulong ComputeVariantHash(ITypeSymbol variantType, ITypeSymbol componentType)
        {
            return ComputeVariantHash(
                Roslyn.Extensions.GetMetadataQualifiedName(variantType),
                Roslyn.Extensions.GetMetadataQualifiedName(componentType));
        }

        public static ulong ComputeVariantHash(string variantTypeFullname, string componentTypeFullName)
        {
            var hash = Utilities.TypeHash.FNV1A64("NetCode.GhostNetVariant");
            hash = Utilities.TypeHash.CombineFNV1A64(hash, Utilities.TypeHash.FNV1A64(componentTypeFullName));
            hash = Utilities.TypeHash.CombineFNV1A64(hash, Utilities.TypeHash.FNV1A64(variantTypeFullname));
            return hash;
        }

        public static SourceText WithInitialLineDirective(this SourceText sourceText, string generatedSourceFilePath)
        {
            var firstLine = sourceText.Lines.FirstOrDefault();
            return sourceText.WithChanges(new TextChange(firstLine.Span, $"#line 1 \"{generatedSourceFilePath}\"" + Environment.NewLine + firstLine));
        }
    }

    internal static class Debug
    {
        public static string LastErrorLog { get; set; } // 供测试使用，TODO: 改为能够跟踪全部日志的完整实现
        public static void LaunchDebugger()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Debugger.Launch();
            }
            else
            {
                string text = $"Attach to {Process.GetCurrentProcess().Id} netcode generator";

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    StarProcess("/usr/bin/osascript", $"-e \"display dialog \\\"{text}\\\" with icon note buttons {{\\\"OK\\\"}}\"");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    StarProcess("/usr/bin/zenity", $@"--info --title=""Attach Debugger"" --text=""{text}"" --no-wrap");
                }
            }
        }

        public static void LaunchDebugger(GeneratorExecutionContext context, string assembly)
        {
            if(string.IsNullOrEmpty(assembly)
               || string.IsNullOrEmpty(context.Compilation.AssemblyName)
               || context.Compilation.AssemblyName.Equals(assembly, StringComparison.InvariantCultureIgnoreCase))
            {
                LaunchDebugger();
            }
        }

        public static void LaunchDebugger(Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax node, string[] names)
        {
            if(names.Contains(node.Identifier.ValueText))
            {
                LaunchDebugger();
            }
        }

        private static void StarProcess(string fileName, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = true,
                CreateNoWindow = false
            };

            var processTemp = new Process {StartInfo = startInfo, EnableRaisingEvents = true};
            processTemp.Start();
            processTemp.WaitForExit();
        }

        private const string LogFile = "SourceGenerator.log";
        private static string GetLogFilePath()
        {
            return Path.Combine(Helpers.GetOutputPath(), LogFile);
        }
        private static TextWriter GetOutputStream()
        {
            return Helpers.WriteLogToDisk ? File.AppendText(GetLogFilePath()) : Console.Out;
        }
        static void LogToDebugStream(string level, string message)
        {
            try
            {
                using var writer = GetOutputStream();
                writer.WriteLine($"[{level}]{message}");
            }
            catch (Exception flushEx)
            {
                Console.WriteLine($"Exception while writing to log: {flushEx.Message}");
            }
        }
        public static void LogException(Exception exception)
        {
            LastErrorLog = exception.ToString();
            try
            {
                using var writer = GetOutputStream();
                writer.Write("[Exception]");
                writer.WriteLine(exception.ToString());
                writer.WriteLine("Callstack:");
                writer.Write(exception.StackTrace);
                writer.Write('\n');
            }
            catch (Exception flushEx)
            {
                Console.WriteLine($"Exception while writing to log: {flushEx.Message}");
            }
        }
        public static void LogDebug(string message)
        {
            if(Helpers.CurrentLogLevel > Helpers.LoggingLevel.Debug)
                return;
            LogToDebugStream("Debug", message);
        }
        public static void LogInfo(string message)
        {
            if(Helpers.CurrentLogLevel > Helpers.LoggingLevel.Info)
                return;
            LogToDebugStream("Info", message);
        }
        public static void LogWarning(string message)
        {
            if(Helpers.CurrentLogLevel > Helpers.LoggingLevel.Warning)
                return;
            LogToDebugStream("Warning", message);
        }
        public static void LogError(string message, string additionalInfo)
        {
            message += $"\n\nAdditional info:\n{additionalInfo}\n\nStacktrace:\n";
            message += Environment.StackTrace;
            LastErrorLog = message;
            LogToDebugStream("Error", message);
        }
    }
}
