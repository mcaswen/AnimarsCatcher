using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Unity.NetCode.Generators
{
    public static class GlobalOptions
    {
        /// <summary>
        /// 覆盖当前项目路径，供生成器写入日志或查找文件
        /// </summary>
        public const string ProjectPath = "unity.netcode.sourcegenerator.projectpath";
        /// <summary>
        /// 覆盖生成器写入日志与生成文件的输出目录
        /// </summary>
        public const string OutputPath = "unity.netcode.sourcegenerator.outputfolder";
        /// <summary>
        /// 跳过缺失程序集引用的验证，主要用于测试
        /// </summary>
        public const string DisableRerencesChecks = "unity.netcode.sourcegenerator.disable_references_checks";
        /// <summary>
        /// 启用或禁用通过 Additional File 传入自定义 Template，主要用于测试
        /// </summary>
        public const string TemplateFromAdditionalFiles = "unity.netcode.sourcegenerator.templates_from_additional_files";
        /// <summary>
        /// 启用或禁用将生成代码写入输出目录
        /// </summary>
        public const string WriteFilesToDisk = "unity.netcode.sourcegenerator.write_files_to_disk";
        /// <summary>
        /// 启用或禁用将日志写入文件，默认为 Temp/NetCodeGenerated/sourcegenerator.log
        /// </summary>
        public const string WriteLogsToDisk = "unity.netcode.sourcegenerator.write_logs_to_disk";
        /// <summary>
        /// 最低日志级别，可选 Debug、Warning、Error，默认为 Error，目前尚未支持
        /// </summary>
        public const string LoggingLevel = "unity.netcode.sourcegenerator.logging_level";
        /// <summary>
        /// 启用或禁用输出 Source Generator 耗时统计
        /// </summary>
        public const string EmitTimings = "unity.netcode.sourcegenerator.emit_timing";
        /// <summary>
        /// 启用或禁用 Source Generator 自动附加调试器
        /// </summary>
        public const string AttachDebugger = "unity.netcode.sourcegenerator.attach_debugger";

        ///<summary>
        /// 返回 GlobalOptions 字典中指定标志是否已设置
        /// 键存在且字符串值为空、"1" 或 "true" 时视为已设置，否则视为未设置
        ///</summary>
        public static bool GetOptionsFlag(this  GeneratorExecutionContext context, string key, bool defaultValue=false)
        {
            if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue(key, out var stringValue))
                return string.IsNullOrEmpty(stringValue) || (stringValue is "1" or "true");
            return defaultValue;
        }

        /// <summary>
        /// 返回 GlobalOptions 中指定键关联的字符串，不存在时返回默认值
        /// </summary>
        /// <param name="context">Generator 执行 Context</param>
        /// <param name="key">配置键</param>
        /// <param name="defaultValue">键不存在时的默认值</param>
        /// <returns>配置字符串或默认值</returns>
        public static string GetOptionsString(this GeneratorExecutionContext context, string key, string defaultValue=null)
        {
            if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue(key, out var stringValue))
                return stringValue;
            return defaultValue;
        }
    }

    /// <summary>
    /// 使用 <see cref="NetCodeSyntaxReceiver"/> 解析语法树，并生成 RPC、Command 与 Ghost 序列化代码
    /// 必须保持无状态且不可变，以支持多线程调用或实例复用
    /// </summary>
    [Generator]
    public class NetCodeSourceGenerator : ISourceGenerator
    {
        internal struct Candidates
        {
            public List<SyntaxNode> Components;
            public List<SyntaxNode> Rpcs;
            public List<SyntaxNode> Commands;
            public List<SyntaxNode> Inputs;
            public List<SyntaxNode> Variants;
        }

        public const string NETCODE_ADDITIONAL_FILE = ".NetCodeSourceGenerator.additionalfile";

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new NetCodeSyntaxReceiver());
            // 在此初始化 Profiler 会把与 Source Generator 无直接关系的 Unity 内部编译耗时也纳入统计
            // 这样可以衡量生成器耗时占总编译时间的比例
            Profiler.Initialize();
        }

        static bool ShouldRunGenerator(GeneratorExecutionContext executionContext)
        {
            // Compilation 未引用 NetCode 时跳过运行
            return executionContext.Compilation.Assembly.Name.StartsWith("Unity.NetCode", StringComparison.Ordinal) ||
                   executionContext.Compilation.ReferencedAssemblyNames.Any(r=>
                       r.Name.Equals("Unity.NetCode", StringComparison.Ordinal) ||
                       r.Name.Equals("Unity.NetCode.ref", StringComparison.Ordinal));
        }

        /// <summary>
        /// Roslyn 在语法分析完成后调用的主入口
        /// 此时应已收集全部候选节点
        /// </summary>
        /// <param name="executionContext">Generator 执行 Context</param>
        public void Execute(GeneratorExecutionContext executionContext)
        {
            executionContext.CancellationToken.ThrowIfCancellationRequested();

            if (!ShouldRunGenerator(executionContext))
                return;

            Helpers.SetupContext(executionContext);
            var diagnostic = new DiagnosticReporter(executionContext);
            diagnostic.LogInfo($"Begin Processing assembly {executionContext.Compilation.AssemblyName}");

            // attach_debugger 键存在但没有值时，返回空字符串而不是 null
            var debugAssembly = executionContext.GetOptionsString(GlobalOptions.AttachDebugger);
            if(debugAssembly != null)
            {
                Debug.LaunchDebugger(executionContext, debugAssembly);
            }
            try
            {
                Generate(executionContext, diagnostic);
            }
            catch (Exception e)
            {
                diagnostic.LogException(e);
            }
            diagnostic.LogInfo($"End Processing assembly {executionContext.Compilation.AssemblyName}.");
            diagnostic.LogInfo(Profiler.PrintStats(executionContext.GetOptionsFlag(GlobalOptions.EmitTimings)));
        }

        private static void Generate(GeneratorExecutionContext executionContext, IDiagnosticReporter diagnostic)
        {
            // 根据结构体实现的接口，将未分类候选节点分派到对应数组
            var receiver = (NetCodeSyntaxReceiver)executionContext.SyntaxReceiver;
            var candidates = ResolveCandidates(executionContext, receiver, diagnostic);
            var totalCandidates = candidates.Rpcs.Count + candidates.Commands.Count + candidates.Components.Count + candidates.Variants.Count + candidates.Inputs.Count;
            if (totalCandidates == 0)
                return;

            // 初始化 Template Registry 并注册用户自定义类型定义
            ImportTemplates(executionContext, diagnostic, out var templateFileProvider);
            var codeGenerationContext = new CodeGenerator.Context(templateFileProvider, diagnostic, executionContext, executionContext.Compilation.AssemblyName);
            // 从这里开始生成 Ghost、Command 与 RPC
            // 遍历语义模型并检查必要条件，再把提取出的 TypeInformation 传给自定义代码生成系统
            using (new Profiler.Auto("Generate"))
            {
                // 为 Input Data 生成 Command Data 包装类型以及 CopyToBuffer 与 CopyFromBuffer System
                using(new Profiler.Auto("InputGeneration"))
                    InputFactory.Generate(candidates.Inputs, codeGenerationContext, executionContext);
                // 为 Component 与 Buffer 生成 Serializer
                using (new Profiler.Auto("ComponentGeneration"))
                    ComponentFactory.Generate(candidates.Components, candidates.Variants, codeGenerationContext);
                // 为 RPC 与 Command 生成 Serializer
                using(new Profiler.Auto("CommandsGeneration"))
                    CommandFactory.Generate(candidates.Commands, codeGenerationContext);
                using(new Profiler.Auto("RpcGeneration"))
                    RpcFactory.Generate(candidates.Rpcs, codeGenerationContext);
            }
            if (codeGenerationContext.batch.Count > 0)
            {
                if(!executionContext.GetOptionsFlag(GlobalOptions.DisableRerencesChecks))
                {
                    // 确保程序集具有必要引用，缺失引用时按致命错误处理
                    var missingReferences = new HashSet<string>{"Unity.Collections", "Unity.Burst", "Unity.Mathematics"};
                    foreach (var r in executionContext.Compilation.ReferencedAssemblyNames)
                        missingReferences.Remove(r.Name);
                    if (missingReferences.Count > 0)
                    {
                        codeGenerationContext.diagnostic.LogError(
                            $"Assembly {executionContext.Compilation.AssemblyName} contains NetCode replicated types. The serialization code will use " +
                            $"burst, collections, mathematics and network data streams but the assembly does not have references to: {string.Join(",", missingReferences)}. " +
                            $"Please add the missing references in the asmdef for {executionContext.Compilation.AssemblyName}.");
                    }
                }
            }
            AddGeneratedSources(executionContext, codeGenerationContext);
        }

        private static void ImportTemplates(GeneratorExecutionContext executionContext, IDiagnosticReporter diagnostic,
            out TemplateRegistry templateRegistry)
        {
            HashSet<string> generatorTemplates = new()
            {
                CodeGenerator.RpcSerializer,
                CodeGenerator.CommandSerializer,
                CodeGenerator.ComponentSerializer,
                CodeGenerator.RegistrationSystem,
                CodeGenerator.InputSynchronization,
                CodeGenerator.GhostFixedListElement,
                CodeGenerator.GhostFixedListContainer,
                CodeGenerator.GhostFixedListCommandHelper,
                CodeGenerator.GhostFixedListSnapshotHelpers,
            };
            List<TypeRegistryEntry> allFieldTemplates = new List<TypeRegistryEntry>(DefaultTypes.Registry);
            using (new Profiler.Auto("LoadRegistryAndOverrides"))
            {
                allFieldTemplates.AddRange(UserDefinedTemplateRegistryParser.ParseTemplates(executionContext, diagnostic));
            }
            // Unity 2021.2 及更高版本始终通过 Additional File 提供额外 Template
            // Template 文件必须以 .netcode.additionalfile 为扩展名
            templateRegistry = new TemplateRegistry(diagnostic);
            templateRegistry.AddTypeTemplates(allFieldTemplates);
            templateRegistry.AddAdditionalTemplates(executionContext.AdditionalFiles, allFieldTemplates, generatorTemplates);
        }

        /// <summary>
        /// 将类型不明确的语法节点映射为代码生成候选类型
        /// </summary>
        /// <param name="executionContext">Generator 执行 Context</param>
        /// <param name="receiver">语法候选接收器</param>
        /// <param name="diagnostic">诊断报告器</param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        private static Candidates ResolveCandidates(GeneratorExecutionContext executionContext, NetCodeSyntaxReceiver receiver, IDiagnosticReporter diagnostic)
        {
            var candidates = new Candidates
            {
                Components = new List<SyntaxNode>(),
                Rpcs = new List<SyntaxNode>(),
                Commands = new List<SyntaxNode>(),
                Inputs = new List<SyntaxNode>(),
                Variants = receiver.Variants
            };

            foreach (var candidate in receiver.Candidates)
            {
                executionContext.CancellationToken.ThrowIfCancellationRequested();

                var symbolModel = executionContext.Compilation.GetSemanticModel(candidate.SyntaxTree);
                var candidateSymbol = symbolModel.GetDeclaredSymbol(candidate) as ITypeSymbol;
                var allComponentTypes = Roslyn.Extensions.GetAllComponentType(candidateSymbol).ToArray();
                // 未实现有效或已知接口
                if (allComponentTypes.Length == 0)
                    continue;

                // 结构体同时实现多个有效接口时报告错误并跳过代码生成
                if (allComponentTypes.Length > 1)
                {
                    diagnostic.LogError(
                        $"struct {Roslyn.Extensions.GetFullTypeName(candidateSymbol)} cannot implement {string.Join(",", allComponentTypes)} interfaces at the same time",
                        candidateSymbol?.Locations[0]);
                    continue;
                }
                switch (allComponentTypes[0])
                {
                    case ComponentType.Unknown:
                        break;
                    case ComponentType.Component:
                        candidates.Components.Add(candidate);
                        break;
                    case ComponentType.HybridComponent:
                        candidates.Components.Add(candidate);
                        break;
                    case ComponentType.Buffer:
                        candidates.Components.Add(candidate);
                        break;
                    case ComponentType.Rpc:
                        candidates.Rpcs.Add(candidate);
                        break;
                    case ComponentType.CommandData:
                        candidates.Commands.Add(candidate);
                        candidates.Components.Add(candidate);
                        break;
                    case ComponentType.Input:
                        candidates.Inputs.Add(candidate);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return candidates;
        }

        /// <summary>
        /// 将生成的源文件加入当前 Compilation，并在启用时写入磁盘
        /// </summary>
        /// <param name="executionContext">Generator 执行 Context</param>
        /// <param name="codeGenContext">代码生成 Context</param>
        private static void AddGeneratedSources(GeneratorExecutionContext executionContext, CodeGenerator.Context codeGenContext)
        {
            using (new Profiler.Auto("WriteFile"))
            {
                executionContext.CancellationToken.ThrowIfCancellationRequested();
                // 始终删除先前生成的全部文件
                if (Helpers.CanWriteFiles)
                {
                    var outputFolder = Path.Combine(Helpers.GetOutputPath(), $"{executionContext.Compilation.AssemblyName}");
                    if(Directory.Exists(outputFolder))
                        Directory.Delete(outputFolder, true);
                    if(codeGenContext.batch.Count != 0)
                        Directory.CreateDirectory(outputFolder);
                }
                if (codeGenContext.batch.Count == 0)
                    return;

                foreach (var nameAndSource in codeGenContext.batch)
                {
                    executionContext.CancellationToken.ThrowIfCancellationRequested();
                    var sourceText = SourceText.From(nameAndSource.Code, System.Text.Encoding.UTF8);
                    var sourcePath = Path.Combine($"{executionContext.Compilation.AssemblyName}",
                        nameAndSource.GeneratedFileName);
                    //var hintName = Utilities.TypeHash.FNV1A64(sourcePath).ToString();
                    // 新版 Roslyn 要求在生成文件首行加入 #line 1 "sourcecodefullpath"
                    // 以便调试时定位到正确文件
                    // 注意：写入磁盘的文件不应包含该 #line 指令，否则调试行号无法正确对应
                    sourcePath = Path.Combine(Helpers.GetOutputPath(), sourcePath);
                    var source = sourceText.WithInitialLineDirective(sourcePath);
                    Debug.LogInfo($"output {nameAndSource.GeneratedFileName} to {sourcePath}");
                    try
                    {
                        if (Helpers.CanWriteFiles)
                            File.WriteAllText(sourcePath, source.ToString());
                    }
                    catch (System.Exception e)
                    {
                        // 极少数写入失败情况下只记录警告并继续，避免中断用户编译流程
                        Debug.LogWarning($"cannot write file {Path.Combine(Helpers.GetOutputPath(), sourcePath)}. An exception has been thrown:{e}");
                    }
                    //var hintName = Utilities.TypeHash.FNV1A64(sourcePath).ToString();
                    executionContext.AddSource(nameAndSource.GeneratedFileName, source);

                }
            }
        }
    }
}
