using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Unity.NetCode.Roslyn;

namespace Unity.NetCode.Generators
{
    internal class ComponentFactory
    {
        /// <summary>
        /// 收集 Component 候选类型并生成序列化代码，同时生成注册 System
        /// </summary>
        /// <param name="componentsCandidates">Component 候选语法节点</param>
        /// <param name="variantsCandidates">Variant 候选语法节点</param>
        /// <param name="codeGenContext">代码生成 Context</param>
        public static void Generate(
            IReadOnlyList<SyntaxNode> componentsCandidates,
            IReadOnlyList<SyntaxNode> variantsCandidates,
            CodeGenerator.Context codeGenContext)
        {
            GenerateComponents(componentsCandidates, codeGenContext);
            GenerateVariants(variantsCandidates, codeGenContext);
            CodeGenerator.GenerateRegistrationSystem(codeGenContext);
        }

        private static void GenerateComponents(IEnumerable<SyntaxNode> components, CodeGenerator.Context codeGenContext)
        {
            var typeBuilder = new TypeInformationBuilder(codeGenContext.diagnostic, codeGenContext.executionContext, TypeInformationBuilder.SerializationMode.Component);
            foreach (var componentCandidate in components)
            {
                codeGenContext.executionContext.CancellationToken.ThrowIfCancellationRequested();

                var syntaxNode = componentCandidate as TypeDeclarationSyntax;
                var hasGhostEnabledBitAttribute = HasGhostEnabledBitAttribute(syntaxNode);
                var hasGhostFields = HasGhostFields(syntaxNode);

                // 注意：这些快速检查仅适用于未继承的特性，无法识别从基类继承的特性
                if (!HasGhostComponentAttribute(syntaxNode) && !hasGhostFields && !hasGhostEnabledBitAttribute)
                    continue;

                Profiler.Begin("GetSemanticModel");
                var model = codeGenContext.executionContext.Compilation.GetSemanticModel(componentCandidate.SyntaxTree);
                var candidateSymbol = model.GetDeclaredSymbol(componentCandidate) as INamedTypeSymbol;
                Profiler.End();
                if (candidateSymbol == null)
                {
                    codeGenContext.diagnostic.LogError($"No INamedTypeSymbol for componentCandidate '{componentCandidate.ToFullString()}'.", syntaxNode.GetLocation());
                    continue;
                }

                var typeNamespace = Roslyn.Extensions.GetFullyQualifiedNamespace(candidateSymbol);
                if (typeNamespace.StartsWith("__COMMAND", StringComparison.Ordinal) ||
                   typeNamespace.StartsWith("__GHOST", StringComparison.Ordinal))
                {
                    codeGenContext.diagnostic.LogError($"Invalid namespace {typeNamespace} for {candidateSymbol.Name}. __GHOST and __COMMAND are reserved prefixes and cannot be used in namspace, type and field names",
                        syntaxNode.GetLocation());
                    continue;
                }

                var ghostComponent = TryGetGhostComponent(candidateSymbol);
                var typeInfo = typeBuilder.BuildTypeInformation(candidateSymbol, ghostComponent);
                if (typeInfo == null)
                    continue;

                // 对需要序列化的 Buffer 与 Command 而言，缺失 GhostField 是错误
                // 在外层集中处理，以便先报告全部错误再跳过该类型
                if (typeBuilder.MissingGhostFields.Count > 0)
                {
                    // 这些字段必须全部标记或全部不标记
                    // 普通 CommandData 或仅有 GhostComponent 标记的 Buffer 可以全部不标记
                    // 但远端玩家 Command Buffer 同步或普通动态 Buffer 一旦有一个字段已标记，就必须全部标记
                    if ((typeInfo.ComponentType == ComponentType.Buffer || typeInfo.ComponentType == ComponentType.CommandData) &&
                        typeInfo.GhostFields.Count > 0)
                    {
                        foreach (var field in typeBuilder.MissingGhostFields)
                            codeGenContext.diagnostic.LogError(
                                $"GhostField missing on field {field}. Buffers must have all fields annotated. CommandData must have none, for normal client to server command stream, or all, as a normal stream and also as a buffer sent from server to other (non-owner) clients.",
                                componentCandidate.GetLocation());
                        typeBuilder.MissingGhostFields.Clear();
                        continue;
                    }
                    typeBuilder.MissingGhostFields.Clear();
                }

                var variantHash = Helpers.ComputeVariantHash(typeInfo.Symbol, typeInfo.Symbol);
                var isSerialized = hasGhostFields || typeInfo.ShouldSerializeEnabledBit;
                codeGenContext.serializationStrategies.Add(new CodeGenerator.Context.SerializationStrategyCodeGen
                {
                    TypeInfo = typeInfo,
                    VariantTypeName = typeInfo.TypeFullName.Replace('+', '.'),
                    ComponentTypeName = typeInfo.TypeFullName.Replace('+', '.'),
                    Hash = variantHash.ToString(),
                    GhostAttribute = ghostComponent,
                    IsSerialized = isSerialized,
                });

                if (!isSerialized)
                    continue;

                codeGenContext.ResetState();
                NameUtils.UpdateNameAndNamespace(typeInfo, ref codeGenContext, candidateSymbol);

                // Serializer 类型已存在时可以跳过生成
                if (codeGenContext.executionContext.Compilation.GetSymbolsWithName(GetGhostSerializerName(codeGenContext)).FirstOrDefault() != null)
                {
                    codeGenContext.diagnostic.LogDebug($"Skipping code-gen for {candidateSymbol.Name} because a component serializer for it already exists");
                    continue;
                }

                codeGenContext.diagnostic.LogInfo($"Generating ghost for {typeInfo.TypeFullName}");
                codeGenContext.types.Add(typeInfo);
                CodeGenerator.GenerateGhost(codeGenContext, typeInfo);
            }
        }

        private static void GenerateVariants(IEnumerable<SyntaxNode> variants, CodeGenerator.Context codeGenContext)
        {
            var typeBuilder = new TypeInformationBuilder(codeGenContext.diagnostic, codeGenContext.executionContext,
                TypeInformationBuilder.SerializationMode.Component);

            foreach (var componentCandidate in variants)
            {
                codeGenContext.executionContext.CancellationToken.ThrowIfCancellationRequested();
                Profiler.Begin("GetSemanticModel");
                var model = codeGenContext.executionContext.Compilation.GetSemanticModel(componentCandidate.SyntaxTree);
                var variantSymbol = model.GetDeclaredSymbol(componentCandidate) as INamedTypeSymbol;
                Profiler.End();
                if (variantSymbol == null)
                    continue;

                var syntaxNode = componentCandidate as TypeDeclarationSyntax;
                var ghostComponent = TryGetGhostComponent(variantSymbol);
                var variation = Roslyn.Extensions.GetAttribute(variantSymbol, "Unity.NetCode", "GhostComponentVariationAttribute");
                var variantTypeInfo = typeBuilder.BuildVariantTypeInformation(variantSymbol, variation, ghostComponent);
                if (variantTypeInfo == null)
                    continue;

                var variantHash = Helpers.ComputeVariantHash(variantSymbol, (ITypeSymbol) variation.ConstructorArguments[0].Value);
                var hasGhostFields = variantTypeInfo.GhostFields.Count != 0;
                var displayName = variation.ConstructorArguments[1].Value;
                if (displayName is not string name || string.IsNullOrWhiteSpace(name))
                    displayName = default;

                var isSerialized = hasGhostFields || variantTypeInfo.ShouldSerializeEnabledBit;
                codeGenContext.serializationStrategies.Add(new CodeGenerator.Context.SerializationStrategyCodeGen
                {
                    TypeInfo = variantTypeInfo,
                    DisplayName = (string)displayName,
                    VariantTypeName = Roslyn.Extensions.GetFullTypeName(variantSymbol).Replace('+', '.'),
                    ComponentTypeName = variantTypeInfo.TypeFullName.Replace('+', '.'),
                    Hash = variantHash.ToString(),
                    GhostAttribute = ghostComponent,
                    IsSerialized = isSerialized,
                });

                if (!isSerialized)
                    continue;

                // 对需要序列化的 Buffer 与 Command 而言，缺失 GhostField 是错误
                // 在外层集中处理，以便先报告全部错误再跳过该类型
                if (variantTypeInfo.ComponentType == ComponentType.Buffer)
                {
                    if (typeBuilder.MissingGhostFields.Count > 0)
                    {
                        foreach (var field in typeBuilder.MissingGhostFields)
                            codeGenContext.diagnostic.LogError($"GhostField missing on field {field} on Variant {variantTypeInfo.TypeFullName}. Buffers or CommandData must have all fields annotated!",
                                syntaxNode.GetLocation());
                        typeBuilder.MissingGhostFields.Clear();
                        continue;
                    }
                }

                codeGenContext.ResetState();
                NameUtils.UpdateNameAndNamespace(variantTypeInfo, ref codeGenContext, variantSymbol);
                // Serializer 类型已存在时可以跳过生成
                if (codeGenContext.executionContext.Compilation.GetSymbolsWithName(GetGhostSerializerName(codeGenContext)).FirstOrDefault() != null)
                {
                    codeGenContext.diagnostic.LogDebug($"Skipping code-gen for {codeGenContext.generatorName} because a variant component serializer for it already exists");
                    continue;
                }

                codeGenContext.types.Add(variantTypeInfo);
                codeGenContext.diagnostic.LogDebug($"Generating serializer for variant {variantSymbol.ToDisplayString()} for type {variantTypeInfo.TypeFullName}.");
                codeGenContext.variantTypeFullName = Roslyn.Extensions.GetFullTypeName(variantSymbol);
                codeGenContext.variantHash = variantHash;
                CodeGenerator.GenerateGhost(codeGenContext, variantTypeInfo);
            }
        }

        /// <summary>
        /// 快速判断类型是否具有 GhostField，以便尽早退出
        /// </summary>
        /// <returns>存在 GhostField 时返回 true</returns>
        static private bool HasGhostFields(TypeDeclarationSyntax structNode)
        {
            using (new Profiler.Auto("HasGhostFields"))
            {
                foreach (var t in structNode.Members
                    .SelectMany(attr => attr.AttributeLists, (attr, list) => list.Attributes)
                    .SelectMany(attributes => attributes))
                {
                    // 移除可能存在的限定名
                    var name = t.Name is QualifiedNameSyntax syntax
                        ? syntax.Right.Identifier.ValueText
                        : t.Name.ToString();
                    if (name == "GhostField" || name == "GhostFieldAttribute")
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 快速判断类型是否具有 GhostEnabledBit，以便尽早退出
        /// </summary>
        /// <returns>存在 GhostEnabledBit 时返回 true</returns>
        static private bool HasGhostEnabledBitAttribute(TypeDeclarationSyntax structNode)
        {
            using (new Profiler.Auto("HasGhostEnabledBitAttribute"))
            {
                foreach (var t in structNode.AttributeLists
                             .SelectMany(list => list.Attributes))
                {
                    // 移除可能存在的限定名
                    var name = t.Name is QualifiedNameSyntax syntax
                        ? syntax.Right.Identifier.ValueText
                        : t.Name.ToString();
                    if (name == "GhostEnabledBit" || name == "GhostEnabledBitAttribute")
                        return true;
                }
                return false;
            }
        }

        static internal bool HasGhostComponentAttribute(TypeDeclarationSyntax structNode)
        {
            using (new Profiler.Auto("HasGhostComponentAttribute"))
            {
                foreach (var t in structNode.AttributeLists
                    .SelectMany(list => list.Attributes))
                {
                    // 移除可能存在的限定名
                    var name = t.Name is QualifiedNameSyntax syntax
                        ? syntax.Right.Identifier.ValueText
                        : t.Name.ToString();
                    if (name == "GhostComponent" || name == "GhostComponentAttribute")
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 检查给定 Symbol 是否具有 GhostComponentAttribute 并解析其配置
        /// </summary>
        /// <returns>解析后的 GhostComponentAttribute，不存在时返回默认值</returns>
        static internal GhostComponentAttribute TryGetGhostComponent(ISymbol symbol)
        {
            using (new Profiler.Auto("TryGetGhostComponent"))
            {
                var attributeData = Roslyn.Extensions.GetAttribute(symbol, "Unity.NetCode", "GhostComponentAttribute");
                if (attributeData == null)
                    return default;
                var ghostAttribute = new GhostComponentAttribute();
                if (attributeData.NamedArguments.Length <= 0)
                    return ghostAttribute;
                var modifierType = typeof(GhostComponentAttribute);
                foreach (var t in attributeData.NamedArguments)
                    modifierType.GetField(t.Key)?.SetValue(ghostAttribute, t.Value.Value);

                return ghostAttribute;
            }
        }

        static private string GetGhostSerializerName(CodeGenerator.Context context)
        {
            return $"{context.generatorName.Replace(".", "").Replace('+', '_')}GhostComponentSerializer";
        }
    }
}
