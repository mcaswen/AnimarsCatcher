using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using System.Linq;
using Unity.NetCode.Roslyn;

namespace Unity.NetCode.Generators
{
    internal class RpcFactory
    {
        /// <summary>
        /// 收集 RPC 候选类型并生成序列化代码
        /// </summary>
        /// <param name="rpcCandidates">RPC 候选语法节点</param>
        /// <param name="codeGenContext">代码生成 Context</param>
        public static void Generate(IReadOnlyList<SyntaxNode> rpcCandidates, CodeGenerator.Context codeGenContext)
        {
            var typeBuilder = new TypeInformationBuilder(codeGenContext.diagnostic, codeGenContext.executionContext, TypeInformationBuilder.SerializationMode.Commands);
            foreach (var syntaxNode in rpcCandidates)
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
                if (candidateSymbol.ImplementsGenericInterface("Unity.NetCode.IRpcCommandSerializer"))
                {
                    codeGenContext.diagnostic.LogInfo($"Skipping code-gen for {candidateSymbol.Name} because an IRpcCommandSerializer for it already exists");
                    continue;
                }
                var typeInfo = typeBuilder.BuildTypeInformation(candidateSymbol, null);
                if (typeInfo == null)
                    continue;
                codeGenContext.ResetState();
                NameUtils.UpdateNameAndNamespace(typeInfo, ref codeGenContext, candidateSymbol);
                // Serializer 类型已存在时可以跳过生成
                if (codeGenContext.executionContext.Compilation.GetSymbolsWithName(GetRpcSerializerName(codeGenContext)).FirstOrDefault() != null)
                {
                    codeGenContext.diagnostic.LogInfo($"Skipping code-gen for {codeGenContext.generatorName} because an rpc serializer for it already exists");
                    continue;
                }

                codeGenContext.types.Add(typeInfo);
                codeGenContext.diagnostic.LogInfo($"Generating rpc for ${typeInfo.TypeFullName}");
                CodeGenerator.GenerateCommand(codeGenContext, typeInfo, CommandSerializer.Type.Rpc);
            }
        }
        static private string GetRpcSerializerName(CodeGenerator.Context context)
        {
            return $"{context.generatorName.Replace(".", "").Replace('+', '_')}Serializer";
        }
    }
}
