using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using System.Linq;
using System;

namespace Unity.NetCode.Generators
{
    internal class CommandFactory
    {
        /// <summary>
        /// 收集 Command 候选类型并生成序列化代码
        /// </summary>
        /// <param name="commandCandidates">Command 候选语法节点</param>
        /// <param name="codeGenContext">代码生成 Context</param>
        public static void Generate(IReadOnlyList<SyntaxNode> commandCandidates, CodeGenerator.Context codeGenContext)
        {
            var typeBuilder = new TypeInformationBuilder(codeGenContext.diagnostic, codeGenContext.executionContext, TypeInformationBuilder.SerializationMode.Commands);
            foreach (var syntaxNode in commandCandidates)
            {
                codeGenContext.executionContext.CancellationToken.ThrowIfCancellationRequested();
                Profiler.Begin("GetSemanticModel");
                var model = codeGenContext.executionContext.Compilation.GetSemanticModel(syntaxNode.SyntaxTree);
                Profiler.End();
                var candidateSymbol = model.GetDeclaredSymbol(syntaxNode) as INamedTypeSymbol;
                if (candidateSymbol == null)
                    continue;

                var disableCommandCodeGen = Roslyn.Extensions.GetAttribute(candidateSymbol,
                    "Unity.NetCode", "NetCodeDisableCommandCodeGenAttribute");
                if (disableCommandCodeGen != null)
                    continue;
                var typeNamespace = Roslyn.Extensions.GetFullyQualifiedNamespace(candidateSymbol);
                if(typeNamespace.StartsWith("__COMMAND", StringComparison.Ordinal) ||
                   typeNamespace.StartsWith("__GHOST", StringComparison.Ordinal))
                {
                    codeGenContext.diagnostic.LogError($"Invalid namespace {typeNamespace} for {candidateSymbol.Name}. __GHOST and __COMMAND are reserved prefixes and cannot be used in namspace, type and field names",
                        syntaxNode.GetLocation());
                    continue;
                }
                var typeInfo = typeBuilder.BuildTypeInformation(candidateSymbol, null);
                if (typeInfo == null)
                    continue;
                codeGenContext.ResetState();
                NameUtils.UpdateNameAndNamespace(typeInfo, ref codeGenContext, candidateSymbol);
                // Serializer 类型已存在时可以跳过生成
                if (codeGenContext.executionContext.Compilation.GetSymbolsWithName(GetCommandSerializerName(codeGenContext)).FirstOrDefault() != null)
                {
                    codeGenContext.diagnostic.LogInfo($"Skipping code-gen for {codeGenContext.generatorName} because a command serializer for it already exists");
                    continue;
                }
                codeGenContext.diagnostic.LogInfo($"Generating command for {typeInfo.TypeFullName}");
                codeGenContext.types.Add(typeInfo);
                CodeGenerator.GenerateCommand(codeGenContext, typeInfo, CommandSerializer.Type.Command);
            }
        }
        static private string GetCommandSerializerName(CodeGenerator.Context context)
        {
            return $"{context.generatorName.Replace(".", "").Replace('+', '_')}Serializer";
        }
    }
}
