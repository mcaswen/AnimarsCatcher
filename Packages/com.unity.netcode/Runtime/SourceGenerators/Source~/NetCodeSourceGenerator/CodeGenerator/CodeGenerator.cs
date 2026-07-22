using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Unity.NetCode.Roslyn;

namespace Unity.NetCode.Generators
{
    // 此类不得保存状态且必须不可变，所有必要数据都应来自参数与 Context
    internal static class CodeGenerator
    {
        public const string RpcSerializer = "NetCode.RpcCommandSerializer.cs";
        public const string CommandSerializer = "NetCode.CommandDataSerializer.cs";
        public const string ComponentSerializer = "NetCode.GhostComponentSerializer.cs";
        public const string RegistrationSystem = "NetCode.GhostComponentSerializerRegistrationSystem.cs";
        public const string InputSynchronization = "NetCode.InputSynchronization.cs";
        public const string GhostFixedListElement = "NetCode.GhostFixedListElement.cs";
        public const string GhostFixedListContainer = "NetCode.GhostFixedListContainer.cs";
        public const string GhostFixedListCommandHelper = "NetCode.GhostFixedListCommandHelper.cs";
        public const string GhostFixedListSnapshotHelpers = "NetCode.GhostFixedListSnapshotHelpers.cs";

        // Namespace 生成存在一些容易混淆的情况
        // 当前生成的命名空间为 AssemblyName.Generated，并遵循以下规则：
        // 1. 类型没有命名空间时，视为位于全局命名空间，无需处理
        // 2. 类型命名空间以 AssemblyName 为共同祖先时，直接使用类型命名空间
        // 3. 类型命名空间与 AssemblyName 没有共同前缀时，直接使用类型命名空间
        // 4. 类型命名空间与 AssemblyName 只有部分共同前缀时，添加 global:: 前缀
        internal static string GetValidNamespaceForType(string generatedNs, string ns)
        {
            // 索引为 0 时仍表示它位于祖先路径上
            if(generatedNs.IndexOf(ns, StringComparison.Ordinal) <= 0)
                return ns;

            // 使用 global 前缀以避免名称解析歧义
            return "global::" + ns;
        }

        public static ReadOnlySpan<byte> Log2DeBruijn => // 32
        [
            00, 09, 01, 10, 13, 21, 02, 29,
            11, 14, 16, 18, 22, 25, 03, 30,
            08, 12, 20, 28, 15, 17, 24, 07,
            19, 27, 23, 06, 26, 05, 04, 31
        ];
        public static int lzcnt(uint value)
        {
            if(value == 0)
                return 32;
            // 将最低置位位以下的所有位填充为 1
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return 31 ^ Log2DeBruijn[(int)((value * 0x07C4ACDDu) >> 27)];
        }

        /// <summary>
        /// 已知 Template 能够生成完全匹配的该类型时返回 true
        /// 返回 false 时会继续检查其子字段能否生成
        /// </summary>
        /// <remarks>
        /// 类型未通过该检查并不意味着无法序列化
        /// 此处只检查是否存在与该类型完全匹配的 Template
        /// </remarks>
        static bool TryGetTypeTemplate(TypeInformation typeInfo, Context context, out TypeTemplate template)
        {
            template = default;
            var description = typeInfo.Description;
            if (!context.templateProvider.TypeTemplates.TryGetValue(description, out template))
            {
                if (description.Attribute.subtype == 0)
                    return false;

                bool foundSubType = false;
                foreach (var myType in context.templateProvider.TypeTemplates)
                {
                    if (description.Attribute.subtype == myType.Key.Attribute.subtype)
                    {
                        if (description.Key == myType.Key.Key)
                        {
                            foundSubType = true;
                            break;
                        }
                        context.diagnostic.LogError($"'{context.generatorName}' defines a field '{typeInfo.FieldName}' with GhostField configuration '{description}' with a subtype, but subType '{description.Attribute.subtype}' is registered to a different type ('{myType.Key.TypeFullName}'). Thus, ignoring this field. Did you mean to use a different subType?",
                            typeInfo.Location);
                        return false;
                    }
                }
                if (!foundSubType)
                {
                    context.diagnostic.LogError($"'{context.generatorName}' defines a field '{typeInfo.FieldName}' with GhostField configuration '{description}' with a subtype, but this subType has not been registered. Known subTypes are {context.templateProvider.FormatAllKnownSubTypes()}. Please register your SubType Template in the `UserDefinedTemplates` `TypeRegistry` via an `.additionalfile` (see docs).",
                        typeInfo.Location);
                    return false;
                }
                return false;
            }

            if (template.SupportsQuantization && description.Attribute.quantization < 0)
            {
                context.diagnostic.LogError($"'{context.generatorName}' defines a field '{typeInfo.FieldName}' with GhostField configuration '{description}' which requires a quantization value to be specified, but it has not been. Thus, ignoring the field. To fix, add a quantization value to the GhostField attribute constructor.",
                    typeInfo.Location);
                template = default;
                return false;
            }

            if (!template.SupportsQuantization && description.Attribute.quantization > 0)
            {
                context.diagnostic.LogError($"'{context.generatorName}' defines a field '{typeInfo.FieldName}' with GhostField configuration '{description}' that does not support quantization, but has a quantization value specified. Thus, ignoring the field. To fix, remove the quantization value from the GhostField attribute constructor.",
                    typeInfo.Location);
                template = default;
                return false;
            }

            // TODO: 当前不支持同时使用 subtype 与 composite
            // 处理嵌套类型时不会继续传递 subtype=x 信息，因此会使用默认 Variant 并忽略 Variant 指定的 Template
            // 普通 Template 也可能被误设为 composite=true，目前无法检测这种错误
            if (template.Composite && description.Attribute.subtype > 0)
            {
                context.diagnostic.LogError($"'{context.generatorName}' defines a field '{typeInfo.FieldName}' with GhostField configuration '{description}' using an invalid configuration: Subtyped types cannot also be defined as composite, as it is assumed your Template given is the one in use for the whole type. I.e. If you'd like to implement change-bit composition yourself on this type, modify the template directly (at '{template.TemplatePath}').");
                return false;
            }

            context.diagnostic.LogDebug($"'{context.generatorName}' found Template for field '{typeInfo.FieldName}' with GhostField configuration '{description}': '{template}'.");
            return true;
        }

        public static void GenerateRegistrationSystem(Context context)
        {
            // 没有可生成内容时跳过空 System 的创建
            if(context.generatedGhosts.Count == 0 && context.serializationStrategies.Count == 0)
                return;

            using (new Profiler.Auto("GenerateRegistrationSystem"))
            {
                // 生成 Ghost 注册代码
                var registrationSystemCodeGen = context.codeGenCache.GetTemplate(CodeGenerator.RegistrationSystem);
                registrationSystemCodeGen = registrationSystemCodeGen.Clone();
                var replacements = new Dictionary<string, string>(16);

                foreach (var t in context.generatedGhosts)
                {
                    replacements["GHOST_NAME"] = t;
                    registrationSystemCodeGen.GenerateFragment("GHOST_COMPONENT_LIST", replacements);
                }

                int selfIndex = 0;
                foreach (var ss in context.serializationStrategies)
                {
                    var typeInfo = ss.TypeInfo;

                    if (typeInfo == null)
                        throw new InvalidOperationException("Must define TypeInfo when using `serializationStrategies.Add`!");

                    if(ss.Hash == "0")
                        context.diagnostic.LogError($"Setting invalid hash on variantType {ss.VariantTypeName} to {ss.Hash}!");

                    var displayName = ss.DisplayName ?? ss.VariantTypeName;
                    displayName = SmartTruncateDisplayNameForFs64B(displayName);

                    var isDefaultSerializer = string.IsNullOrWhiteSpace(ss.VariantTypeName) || ss.VariantTypeName == ss.ComponentTypeName;

                    replacements["VARIANT_TYPE"] = ss.VariantTypeName;
                    replacements["GHOST_COMPONENT_TYPE"] = ss.ComponentTypeName;
                    replacements["GHOST_VARIANT_DISPLAY_NAME"] = displayName;
                    replacements["GHOST_VARIANT_HASH"] = ss.Hash;
                    replacements["SELF_INDEX"] = selfIndex++.ToString();
                    replacements["VARIANT_IS_SERIALIZED"] = ss.IsSerialized ? "1" : "0";
                    replacements["GHOST_IS_DEFAULT_SERIALIZER"] = isDefaultSerializer ? "1" : "0";
                    replacements["GHOST_SEND_CHILD_ENTITY"] = typeInfo.GhostAttribute != null && typeInfo.GhostAttribute.SendDataForChildEntity ? "1" : "0";
                    replacements["TYPE_IS_INPUT_COMPONENT"] = typeInfo.ComponentType == ComponentType.Input ? "1" : "0";
                    replacements["TYPE_IS_INPUT_BUFFER"] = typeInfo.ComponentType == ComponentType.CommandData ? "1" : "0";
                    replacements["TYPE_IS_TEST_VARIANT"] = typeInfo.IsTestVariant ? "1" : "0";
                    replacements["TYPE_HAS_DONT_SUPPORT_PREFAB_OVERRIDES_ATTRIBUTE"] = typeInfo.HasDontSupportPrefabOverridesAttribute ? "1" : "0";
                    replacements["GHOST_PREFAB_TYPE"] = ss.GhostAttribute != null ? $"GhostPrefabType.{ss.GhostAttribute.PrefabType.ToString().Replace(",", "|GhostPrefabType.")}" : "GhostPrefabType.All";

                    if (typeInfo.GhostAttribute != null)
                    {
                        if ((typeInfo.GhostAttribute.PrefabType & GhostPrefabType.Client) == GhostPrefabType.InterpolatedClient)
                            replacements["GHOST_SEND_MASK"] = "GhostSendType.OnlyInterpolatedClients";
                        else if ((typeInfo.GhostAttribute.PrefabType & GhostPrefabType.Client) == GhostPrefabType.PredictedClient)
                            replacements["GHOST_SEND_MASK"] = "GhostSendType.OnlyPredictedClients";
                        else if (typeInfo.GhostAttribute.PrefabType == GhostPrefabType.Server)
                            replacements["GHOST_SEND_MASK"] = "GhostSendType.DontSend";
                        else if (typeInfo.GhostAttribute.SendTypeOptimization == GhostSendType.OnlyInterpolatedClients)
                            replacements["GHOST_SEND_MASK"] = "GhostSendType.OnlyInterpolatedClients";
                        else if (typeInfo.GhostAttribute.SendTypeOptimization == GhostSendType.OnlyPredictedClients)
                            replacements["GHOST_SEND_MASK"] = "GhostSendType.OnlyPredictedClients";
                        else if (typeInfo.GhostAttribute.SendTypeOptimization == GhostSendType.AllClients)
                            replacements["GHOST_SEND_MASK"] = "GhostSendType.AllClients";
                        else
                            replacements["GHOST_SEND_MASK"] = "GhostSendType.DontSend";
                    }
                    else
                    {
                        replacements["GHOST_SEND_MASK"] = "GhostSendType.AllClients";
                    }

                    registrationSystemCodeGen.GenerateFragment("GHOST_SERIALIZATION_STRATEGY_LIST", replacements);

                    if (typeInfo.ComponentType == ComponentType.Input && !String.IsNullOrEmpty(ss.InputBufferComponentTypeName))
                    {
                        replacements["GHOST_INPUT_BUFFER_COMPONENT_TYPE"] = ss.InputBufferComponentTypeName;

                        registrationSystemCodeGen.GenerateFragment("GHOST_INPUT_COMPONENT_LIST", replacements);
                    }
                }

                replacements.Clear();
                replacements["GHOST_USING"] = context.rootNs;
                registrationSystemCodeGen.GenerateFragment("GHOST_USING_STATEMENT", replacements);

                replacements.Clear();
                replacements.Add("GHOST_NAMESPACE", context.rootNs);
                registrationSystemCodeGen.GenerateFile("GhostComponentSerializerCollection.cs", replacements, context.batch);
            }
        }

        /// <summary>
        /// 从前部截短过长的显示名称，例如依次移除 "Some"、"Very" 等命名空间片段
        /// 结果必须容纳于 FixedString 容量内，否则注册期间会抛出运行时异常
        /// </summary>
        internal static string SmartTruncateDisplayNameForFs64B(string displayName)
        {
            int indexOf = 0;
            const int fixedString64BytesCapacity = 61;
            while (displayName.Length - indexOf > fixedString64BytesCapacity && indexOf < displayName.Length)
            {
                int newIndexOf = displayName.IndexOf('.', indexOf);
                if (newIndexOf < 0) newIndexOf = displayName.IndexOf(',', indexOf);

                // 找不到分隔点时只能从单词中间截断
                if (newIndexOf < 0 || newIndexOf >= displayName.Length - 1)
                    indexOf = Math.Max(0, displayName.Length - fixedString64BytesCapacity);
                else indexOf = newIndexOf + 1;
            }
            return displayName.Substring(indexOf, displayName.Length - indexOf);
        }

        public static void GenerateGhost(Context context, TypeInformation typeTree)
        {
            using(new Profiler.Auto("CodeGen"))
            {
                var generator = new ComponentSerializer(context, typeTree);
                context.root = typeTree;
                GenerateType(context, typeTree, generator, null,typeTree.TypeFullName, 0);
                generator.GenerateSerializer(context, typeTree);
            }
        }

        public static void GenerateCommand(Context context, TypeInformation typeInfo, CommandSerializer.Type commandType)
        {
            void BuildGenerator(Context ctx, TypeInformation fieldType, string root, CommandSerializer parentGenerator)
            {
                if (!fieldType.IsValid)
                    return;

                var fieldGen = new CommandSerializer(context, parentGenerator.CommandType, fieldType);
                if (fieldType.Kind == GenTypeKind.FixedSizeArray)
                {
                    var elementType = fieldType.PointeeType;
                    for (int index = 0; index < fieldType.ElementCount; ++index)
                    {
                        elementType.FieldName = $"{fieldType.FieldName}[{index}]";
                        BuildGenerator(ctx, elementType, root, fieldGen);
                    }
                    fieldGen.AppendTarget(parentGenerator);
                    return;
                }
                if (TryGetTypeTemplate(fieldType, context, out var template))
                {
                    if (!template.SupportCommand)
                        return;

                    fieldGen = new CommandSerializer(context, parentGenerator.CommandType, fieldType, template);
                    if (fieldType.Kind == GenTypeKind.FixedList && parentGenerator.CommandType != Generators.CommandSerializer.Type.Input)
                    {
                        // 在临时容器中为参数构建 Command 读写代码
                        var fixedListArgType = fieldType.PointeeType;
                        // 参数自身没有访问路径，完整路径由外层生成器提供
                        var fixedListArgGen = new CommandSerializer(context, parentGenerator.CommandType, fixedListArgType);
                        BuildGenerator(ctx, fixedListArgType, String.Empty, fixedListArgGen);
                        fieldGen.GenerateFixedListField(context, fixedListArgGen, fieldType, root);
                        fieldGen.AppendTarget(parentGenerator);
                        return;
                    }
                    if (!template.Composite)
                    {
                        fieldGen.GenerateFields(ctx, root, fieldType);
                        fieldGen.AppendTarget(parentGenerator);
                        return;
                    }
                }

                foreach (var field in fieldType.GhostFields)
                {
                    BuildGenerator(ctx, field, root, fieldGen);
                }

                fieldGen.AppendTarget(parentGenerator);
            }

            using(new Profiler.Auto("CodeGen"))
            {
                context.root = typeInfo;
                var serializeGenerator = new CommandSerializer(context, commandType);
                BuildGenerator(context, typeInfo, "", serializeGenerator);
                serializeGenerator.GenerateSerializer(context, typeInfo);
                if (commandType == Generators.CommandSerializer.Type.Input)
                {
                    // Input Component 需要注册为空类型 Variant
                    // 以便在 Ghost 转换期间解析其 Ghost Component 特性
                    var inputGhostAttributes = ComponentFactory.TryGetGhostComponent(typeInfo.Symbol);
                    if (inputGhostAttributes == null)
                        inputGhostAttributes = new GhostComponentAttribute();
                    var variantHash = Helpers.ComputeVariantHash(typeInfo.Symbol, typeInfo.Symbol);
                    context.serializationStrategies.Add(new CodeGenerator.Context.SerializationStrategyCodeGen
                    {
                        TypeInfo = typeInfo,
                        VariantTypeName = typeInfo.TypeFullName.Replace('+', '.'),
                        ComponentTypeName = typeInfo.TypeFullName.Replace('+', '.'),
                        Hash = variantHash.ToString(),
                        GhostAttribute = inputGhostAttributes
                    });

                    TypeInformation bufferTypeTree;
                    ITypeSymbol bufferSymbol;
                    string bufferName;
                    using (new Profiler.Auto("GenerateInputBufferType"))
                    {
                        if (!GenerateInputBufferType(context, typeInfo, out bufferTypeTree,
                                out bufferSymbol, out bufferName))
                            return;
                    }

                    var tmp = context.serializationStrategies[context.serializationStrategies.Count-1];
                    tmp.InputBufferComponentTypeName = bufferSymbol.ToDisplayString();
                    context.serializationStrategies[context.serializationStrategies.Count-1] = tmp;

                    using (new Profiler.Auto("GenerateInputCommandData"))
                    {
                        serializeGenerator = new CommandSerializer(context, Generators.CommandSerializer.Type.Command);
                        BuildGenerator(context, bufferTypeTree, "", serializeGenerator);
                        serializeGenerator.GenerateSerializer(context, bufferTypeTree);
                    }

                    using (new Profiler.Auto("GenerateInputBufferGhostComponent"))
                    {
                        // 检查 Input 类型是否具有 GhostField 特性
                        // 需要先从候选列表查找 Symbol，再从中获取字段成员
                        bool hasGhostFields = false;
                        foreach (var member in typeInfo.Symbol.GetMembers())
                        {
                            foreach (var attribute in member.GetAttributes())
                            {
                                if (attribute.AttributeClass != null &&
                                    attribute.AttributeClass.Name is "GhostFieldAttribute" or "GhostField")
                                    hasGhostFields = true;
                            }
                        }


                        // 将生成的 Input Buffer 解析为 Component，使其纳入 Snapshot 复制
                        // 仅当 Input 结构体包含 Ghost Field 时才需要这样做
                        // 此时生成的 Input Buffer 应复制给远端玩家
                        if (hasGhostFields) // Input 不支持 GhostEnabledBit，因此这里无需考虑
                        {
                            GenerateInputBufferGhostComponent(context, typeInfo, bufferName, bufferSymbol);
                        }
                        else
                        {
                            // 即使没有 Ghost Field 也必须添加序列化策略
                            // 因为空 Variant 仍会保存 Ghost Component 特性
                            var bufferVariantHash = Helpers.ComputeVariantHash(bufferTypeTree.Symbol, bufferTypeTree.Symbol);
                            context.diagnostic.LogDebug($"Adding SerializationStrategy for input buffer {bufferTypeTree.TypeFullName}, which doesn't have any GhostFields, as we still need to store the GhostComponentAttribute data.");
                            context.serializationStrategies.Add(new CodeGenerator.Context.SerializationStrategyCodeGen
                            {
                                TypeInfo = typeInfo,
                                IsSerialized = false,
                                VariantTypeName = bufferTypeTree.TypeFullName.Replace('+', '.'),
                                ComponentTypeName = bufferTypeTree.TypeFullName.Replace('+', '.'),
                                Hash = bufferVariantHash.ToString(),
                                GhostAttribute = inputGhostAttributes
                            });
                        }
                    }
                }
            }
        }

        #region Internal for Code Generation

        private static bool GenerateInputBufferType(Context context, TypeInformation typeTree, out TypeInformation bufferTypeTree, out ITypeSymbol bufferSymbol, out string bufferName)
        {
            // TODO: 代码生成应为带有 [GhostEnabledBit] 的零大小 Buffer 抛出异常

            // 将 Command 类型 Symbol 的生成代码加入 Compilation 以供后续处理
            // 首先查询元数据缓存，如果已存在即可直接使用
            var bufferType = context.executionContext.Compilation.GetTypeByMetadataName("Unity.NetCode.InputBufferData`1");
            var inputType = typeTree.Symbol;
            if (bufferType == null)
            {
                // 在当前 Compilation Unit 中查找，这是慢速路径
                // 只会发生在 NetCode 程序集自身，且其中没有 IInputComponentData，因此影响可接受
                var inputBufferType = context.executionContext.Compilation.GetSymbolsWithName("InputBufferData", SymbolFilter.Type).First() as INamedTypeSymbol;
                bufferSymbol = inputBufferType.Construct(inputType);
            }
            else
            {
                bufferSymbol = bufferType.Construct(inputType);
            }
            if (bufferSymbol == null)
            {
                context.diagnostic.LogError($"Failed to construct input buffer symbol InputBufferData<{typeTree.TypeFullName}>!");
                bufferTypeTree = null;
                bufferName = null;
                return false;
            }
            // FieldTypeName 包含命名空间，生成 Buffer 类型名称时将其移除
            bufferName = $"{typeTree.FieldTypeName}InputBufferData";

            if (typeTree.Namespace.Length != 0 && typeTree.FieldTypeName.Length > typeTree.Namespace.Length)
                bufferName = $"{typeTree.FieldTypeName.Substring(typeTree.Namespace.Length + 1)}InputBufferData";
            // 如果类型嵌套于其他类或类型中，则将父类型名称以底线分隔后并入类型名称
            bufferName = bufferName.Replace('.', '_');

            var typeBuilder = new TypeInformationBuilder(context.diagnostic, context.executionContext, TypeInformationBuilder.SerializationMode.Commands);
            // 将生成的 Input 代码解析为 Command Data
            context.ResetState();
            context.generatedFilePrefix += bufferName;
            bufferName = context.generatorName + bufferName;
            context.generatorName = bufferName;

            bufferTypeTree = typeBuilder.BuildTypeInformation(bufferSymbol, null);
            if (bufferTypeTree == null)
            {
                context.diagnostic.LogError($"Failed to generate type information for symbol ${bufferSymbol.ToDisplayString()}!");
                return false;
            }
            context.types.Add(bufferTypeTree);
            context.diagnostic.LogDebug($"Generating input buffer command data for ${bufferTypeTree.TypeFullName}!");
            return true;
        }

        private static void GenerateInputBufferGhostComponent(Context context, TypeInformation inputTypeTree, string bufferName, ITypeSymbol bufferSymbol)
        {
            // 加入 generatedTypes 列表，使其包含在 Serializer 注册 System 中
            context.generatedGhosts.Add($"global::{context.generatedNs}.{bufferName}");

            var ghostFieldOverride = new GhostField();
            // 重新构建类型信息，并将该类型解释为 Component 而非 Command
            var typeBuilder = new TypeInformationBuilder(context.diagnostic, context.executionContext, TypeInformationBuilder.SerializationMode.Component);
            context.ResetState();
            var bufferTypeTree = typeBuilder.BuildTypeInformation(bufferSymbol, null, ghostFieldOverride);
            if (bufferTypeTree == null)
            {
                context.diagnostic.LogError($"Failed to generate type information for symbol ${bufferSymbol.ToDisplayString()}!");
                return;
            }
            // 使用 Input Component 源码中设置的值或默认值配置 Ghost Component 特性
            // 唯一例外是动态 Buffer 的 OwnerSendType 只能为 SendToNonOwner
            var inputGhostAttributes = ComponentFactory.TryGetGhostComponent(inputTypeTree.Symbol);
            if (inputGhostAttributes != null)
            {
                bufferTypeTree.GhostAttribute = new GhostComponentAttribute
                {
                    PrefabType = inputGhostAttributes.PrefabType,
                    SendDataForChildEntity = inputGhostAttributes.SendDataForChildEntity,
                    SendTypeOptimization = inputGhostAttributes.SendTypeOptimization,
                    OwnerSendType = SendToOwnerType.SendToNonOwner
                };
            }
            else
                bufferTypeTree.GhostAttribute = new GhostComponentAttribute { OwnerSendType = SendToOwnerType.SendToNonOwner };

            var variantHash = Helpers.ComputeVariantHash(bufferTypeTree.Symbol, bufferTypeTree.Symbol);
            context.serializationStrategies.Add(new CodeGenerator.Context.SerializationStrategyCodeGen
            {
                TypeInfo = bufferTypeTree,
                VariantTypeName = bufferTypeTree.TypeFullName.Replace('+', '.'),
                ComponentTypeName = bufferTypeTree.TypeFullName.Replace('+', '.'),
                Hash = variantHash.ToString(),
                GhostAttribute = bufferTypeTree.GhostAttribute,
                IsSerialized = true,
            });

            context.types.Add(bufferTypeTree);
            context.diagnostic.LogDebug($"Generating ghost for input buffer {bufferTypeTree.TypeFullName}!");
            GenerateGhost(context, bufferTypeTree);
        }

        private static void GenerateType(Context context, TypeInformation type,
            ComponentSerializer parentContainer, string rootPath, string fullFieldName, int fieldIndex)
        {
            context.executionContext.CancellationToken.ThrowIfCancellationRequested();
            if (TryGetTypeTemplate(type, context, out var template))
            {
                if (type.Kind == GenTypeKind.FixedList)
                {
                    GenerateFixedListField(context, type, parentContainer, template, fieldIndex, rootPath);
                    context.curChangeMaskBits += 2;
                    context.changeMaskBitCount += 2;
                }
                else if (template.Composite)
                {
                    if(!GenerateCompositeField(context, type, parentContainer, template, rootPath))
                        return;
                }
                else
                {
                    var generator = new ComponentSerializer(context, type, template);
                    generator.GenerateFields(context, rootPath);
                    // TODO: 尚不支持多位 Template，FixedList 是例外并采用稍有不同的处理方式
                    generator.GenerateMasks(context, 1, type.Attribute.aggregateChangeMask, fieldIndex);
                    generator.AppendTarget(parentContainer);
                }

                if (!parentContainer.TypeInformation.Attribute.aggregateChangeMask)
                {
                    // 如果父类型不聚合字段，则需要同时增加当前 ChangeMask 位计数和总位计数
                    parentContainer.m_TargetGenerator.AppendFragment("GHOST_AGGREGATE_WRITE", parentContainer.m_TargetGenerator, "GHOST_WRITE_COMBINED");
                    parentContainer.m_TargetGenerator.Fragments["__GHOST_AGGREGATE_WRITE__"].Content = "";
                    ++context.curChangeMaskBits;
                    ++context.changeMaskBitCount;
                }
                return;
            }
            if (type.Kind == GenTypeKind.FixedSizeArray)
            {
                var elementType = type.PointeeType;
                for (var index = 0; index < type.ElementCount; index++)
                {
                    // 此处需要区分访问路径
                    // 固定 Buffer 的参数必然是基础类型，因此使用简化逻辑直接序列化第 X 个元素
                    // C# 11 支持结构体固定 Buffer 后，可能需要改为更接近 FixedList 的方案并增加辅助类型
                    elementType.FieldName = $"{type.FieldName}[{index}]";
                    elementType.SnapshotFieldName = $"{type.FieldName}_{index}";
                    GenerateType(context, elementType, parentContainer, rootPath,$"{elementType.ContainingTypeFullName}.{elementType.FieldName}", index);
                }
            }
            else
            {
                // 基础类型仍未找到可用 Template 时无法继续向下展开，应当报错
                var isErrorBecausePrimitive = type.Kind == GenTypeKind.Primitive;
                var isErrorBecauseMustFindSubType = type.Description.Attribute.subtype != 0;
                if (isErrorBecausePrimitive || isErrorBecauseMustFindSubType)
                {
                    context.diagnostic.LogError($"Inside type '{context.generatorName}', we could not find the exact template for field '{type.FieldName}' with configuration '{type.Description}', which means that netcode cannot serialize this type (with this configuration), as it does not know how. " +
                                                $"To rectify, either a) define your own template for this type (and configuration), b) resolve any other code-gen errors, or c) modify your GhostField(...) configuration (Quantization, SubType, SmoothingAction etc) to use a known, already existing template. Known templates are {context.templateProvider.FormatAllKnownTypes()}. All known subTypes are {context.templateProvider.FormatAllKnownSubTypes()}!", type.Location);
                    return;
                }
                if (type.GhostFields.Count == 0 && !type.ShouldSerializeEnabledBit)
                {
                    context.diagnostic.LogError($"Couldn't find the TypeDescriptor for GhostField '{context.generatorName}.{type.FieldName}' the type {type.Description} when processing {fullFieldName}! Types must have either valid [GhostField] attributes, or a [GhostEnabledBit] (on an IEnableableComponent).", type.Location);
                    return;
                }

                // 创建临时容器以汇总当前生成代码
                var temp = new ComponentSerializer(context, type);
                for (var index = 0; index < type.GhostFields.Count; index++)
                {
                    var field = type.GhostFields[index];
                    GenerateType(context, field, temp, rootPath,$"{field.ContainingTypeFullName}.{field.FieldName}", index);
                }
                temp.AppendTarget(parentContainer);
            }
            // 当前聚合范围结束时增加 ChangeMask 位计数
            if (type.Attribute.aggregateChangeMask && !parentContainer.TypeInformation.Attribute.aggregateChangeMask)
            {
                parentContainer.m_TargetGenerator.AppendFragment("GHOST_AGGREGATE_WRITE", parentContainer.m_TargetGenerator, "GHOST_WRITE_COMBINED");
                parentContainer.m_TargetGenerator.Fragments["__GHOST_AGGREGATE_WRITE__"].Content = "";
                ++context.curChangeMaskBits;
                ++context.changeMaskBitCount;
            }
        }

        // FixedList ChangeMask 的位数至少有两种设计方式
        // 最直观的方案是用 1 位表示长度，再为每个元素的每个字段各用 1 位
        // 但这很容易让所需位数无意义地快速膨胀
        // 此处针对常见用法优化：
        // - 用户通常会向 Buffer 添加或移除元素
        // - 很少只修改单个字段
        // - 元素最终通常会被整体移除或整体修改，而非局部修改
        // 因此采用以下布局：
        // - 每个元素使用 1 位，并聚合该元素的全部字段
        // - 长度使用 1 位

        // TODO: 即使该方案的读写性能大致等同甚至优于 Buffer，许多情况下仍有过多位移操作
        // 可考虑以下改进：
        // - 将允许的最大容量限制为 64 或 128 个元素，这一上限合理且能简化逻辑
        // - 一次性读取 FixedList 容量对应的全部 Mask 位
        //
        // 更一般地，将 Component 允许的字段数限制为 64 或 128，可以在几乎不损失灵活性的情况下进一步简化序列化循环
        // 128 个字段已经是很高且合理的上限
        // 此外始终将全部 Mask 重置为 0 可以消除大量位移，因为位移执行频率通常远高于 Mask 重置
        private static void GenerateFixedListField(Context context, TypeInformation fixedListType, ComponentSerializer parentContainer,
            TypeTemplate template, int fieldIndex, string rootPath)
        {
            // 首先把参数类型视为根类型进行生成
            // 结果用于构造一个在当前程序集内临时保存字段数据的结构体
            var fixedListFieldType = fixedListType.PointeeType;
            var argumentContainer = new ComponentSerializer(context, fixedListFieldType);
            var isPrimitive = argumentContainer.TypeInformation.Kind != GenTypeKind.Struct;
            context.PushState();
            context.changeMaskBitCount = 0;
            context.curChangeMaskBits = 0;
            // 先在独立的临时 Template 容器中生成参数类型
            // 这样可以确定序列化单个元素所需的位数
            // TODO: 考虑将 Component 位掩码容量硬性限制为最多 128 位，这会大幅简化整体实现
            fixedListFieldType.Attribute.aggregateChangeMask = true;
            GenerateType(context, fixedListFieldType,  argumentContainer, null, fixedListFieldType.TypeFullName,0);
            context.PopState();

            // 为所有情况生成序列化辅助代码，包括基础类型
            var fixedListElementHelperGen = context.codeGenCache.GetTemplate(CodeGenerator.GhostFixedListSnapshotHelpers).Clone();
            // 根据类型描述 Hash、类型全名和字段名生成唯一的 FixedList 泛型参数名称
            // 使用类型描述 Hash 是因为结构体取决于字段参数，例如 Quantization
            // 每种结构体与 GhostField 选项组合只应生成一次
            // 当前按程序集分别生成代码，会导致相同内容跨程序集重复；理想情况应收集全部程序集数据后统一生成
            // 一种替代方案是通过元数据生成序列化 Schema，并使用完全不依赖代码生成的通用序列化机制
            // 该方案的关键问题是评估这种 Serializer 经 Burst 编译后的代码质量
            // 但从可维护性看，它会显著简化自定义类型扩展和跨程序集一致性检查
            // 结构体名称形如 MyType_FixedListFieldName_ArgumentTypeName
            // TODO: 优化为每种类型唯一，而不是每个字段与类型组合唯一，以减少代码膨胀和重复
            string elementHelperPrefix = $"_{argumentContainer.TypeInformation.Description.GetHashCode():x}_{argumentContainer.TypeInformation.TypeFullName}".Replace('.', '_');
            string elementTypeName;
            // 如果类型是结构体，需要额外生成一个以 Snapshot 格式保存元素的结构体
            // 还需要为该结构体生成提供以下能力的 Ghost Serializer：
            // - 与 Snapshot 之间双向复制
            // - 序列化
            // - 反序列化
            // - 计算 ChangeMask
            // 其他情况下可直接使用泛型参数本身
            if (argumentContainer.TypeInformation.Kind == GenTypeKind.Struct)
            {
                elementTypeName = elementHelperPrefix;
                // 将 Ghost Field 加入 FixedList 元素生成片段
                var fixedListElementGen = context.codeGenCache.GetTemplate(CodeGenerator.GhostFixedListElement).Clone();
                fixedListElementGen.Replacements["GHOST_FIELD_TYPE"] = elementTypeName;
                fixedListElementGen.Replacements["GHOST_NAMESPACE"] = context.generatedNs;
                argumentContainer.m_TargetGenerator.AppendFragment("GHOST_FIELD", fixedListElementGen);
                fixedListElementHelperGen.Fragments["__GHOST_FIXEDLIST_ELEMENT__"].Content = fixedListElementGen.GenerateContent(fixedListElementGen.Replacements);
            }
            else
            {
                // 具体类型通常取决于 Quantization 或用户 Template
                // 因此需要直接生成 GHOST_FIELD 片段，并解析生成的替换内容来提取类型信息
                var argField = argumentContainer.m_TargetGenerator.GetFragmentContent("GHOST_FIELD");
                argField = argField.Substring(argField.IndexOf("public", StringComparison.Ordinal));
                // 假定类型是第二个 Token，但仍要验证这一假定
                var args = argField.Split(' ');
                if (args.Length < 3)
                    throw new Exception($"The __GHOST_FIELD__ region for template {template.TemplatePath} used for primitive type {fixedListType.TypeFullName} does not comply with the `public {{c# type}} __GHOST_FIELD_NAME__` format. Please ensure that the region is in this format");
                elementTypeName = args[1];
            }

            fixedListElementHelperGen.Replacements["GHOST_FIXEDLIST_ELEMENT_SERIALIZER"] = $"{elementHelperPrefix}_Serializer";
            fixedListElementHelperGen.Replacements["GHOST_FIXEDLIST_SERIALIZER"] = $"{elementHelperPrefix}_FixedList_Serializer";
            fixedListElementHelperGen.Replacements["GHOST_NAME"] = context.root.TypeFullName;
            fixedListElementHelperGen.Replacements["GHOST_NAMESPACE"] = context.generatedNs;
            fixedListElementHelperGen.Replacements["GHOST_FIELD_TYPE"] = elementTypeName;
            fixedListElementHelperGen.Replacements["GHOST_COMPONENT_TYPE"] = argumentContainer.TypeInformation.FieldTypeName;
            argumentContainer.m_TargetGenerator.AppendFragment("GHOST_COPY_TO_SNAPSHOT", fixedListElementHelperGen);
            argumentContainer.m_TargetGenerator.AppendFragment("GHOST_COPY_FROM_SNAPSHOT", fixedListElementHelperGen);
            argumentContainer.m_TargetGenerator.AppendFragment("GHOST_CALCULATE_CHANGE_MASK", fixedListElementHelperGen);
            argumentContainer.m_TargetGenerator.AppendFragment("GHOST_READ", fixedListElementHelperGen);
            argumentContainer.m_TargetGenerator.AppendFragment("GHOST_WRITE", fixedListElementHelperGen);
            argumentContainer.m_TargetGenerator.AppendFragment("GHOST_AGGREGATE_WRITE", fixedListElementHelperGen);

            if (!context.generatedTypes.Contains(elementHelperPrefix))
            {
                fixedListElementHelperGen.GenerateFile(elementHelperPrefix + "_GhostElement.cs",
                    fixedListElementHelperGen.Replacements, context.batch);
                context.generatedTypes.Add(elementHelperPrefix);
            }
            // 生成表示该列表 Snapshot 格式的 FixedList Snapshot 结构体，形如：
            //struct ParentPath_FieldName {
            // GenArgSnapshot Element0;
            // GenArgSnapshot Element1;
            // GenArgSnapshot Element2;
            // }
            // TODO: 多个 Component 使用相同字段类型时应共享该结构体，以避免代码膨胀
            var fixedListSnapshotField = context.codeGenCache.GetTemplate(CodeGenerator.GhostFixedListContainer).Clone();
            // 先使用生成的结构体或参数类型名称生成全部元素
            fixedListSnapshotField.Replacements["GHOST_ELEMENT_TYPENAME"] = elementTypeName;
            for (int i = 0; i < fixedListType.ElementCount; ++i)
            {
                fixedListSnapshotField.Replacements["GHOST_ELEMENT_FIELD_NAME"] = $"Element{i}";
                fixedListSnapshotField.GenerateFragment("GHOST_FIXEDLIST_ELEMENTS", fixedListSnapshotField.Replacements);
            }
            // TODO: 此内容也可以按类型共享
            var fixedListStructName = $"_{argumentContainer.TypeInformation.Description.GetHashCode():x}_{fixedListType.ContainingTypeFullName}.{fixedListType.FieldName}".Replace('.','_');
            fixedListSnapshotField.Replacements["GHOST_NAME"] = context.root.TypeFullName;
            fixedListSnapshotField.Replacements["GHOST_FIXEDLIST_ELEMENT_SERIALIZER"] = fixedListElementHelperGen.Replacements["GHOST_FIXEDLIST_ELEMENT_SERIALIZER"];
            fixedListSnapshotField.Replacements["GHOST_FIXEDLIST_SERIALIZER"] = fixedListElementHelperGen.Replacements["GHOST_FIXEDLIST_SERIALIZER"];
            fixedListSnapshotField.Replacements["GHOST_FIXEDLIST_NAME"] = fixedListStructName;
            fixedListSnapshotField.Replacements["GHOST_FIXEDLIST_CAPACITY"] = fixedListType.ElementCount.ToString();
            fixedListSnapshotField.Replacements["GHOST_NAMESPACE"] = context.generatedNs;
            fixedListSnapshotField.Replacements["GHOST_COMPONENT_TYPE"] = fixedListType.FieldTypeName;
            fixedListSnapshotField.GenerateFile(fixedListStructName + "_GhostData.cs", fixedListSnapshotField.Replacements, context.batch);

            // 辅助代码就绪后，可以按常规方式生成其余 FixedList 区域
            // TODO: 尚未完整支持多位 Template，FixedList 是例外并采用稍有不同的处理方式
            // FixedList 是多字段、多位 Template：
            // - 1 位表示长度
            // - 1 位表示内容
            // - Snapshot 中每个元素另用 1 位，存储在字段数据而非 ChangeMask 中，以简化逻辑
            // TODO: FixedList 从不参与聚合，因此聚合流程在该字段前停止，并在字段后恢复
            var fixedListGenerator = new ComponentSerializer(context, fixedListType, template);
            fixedListGenerator.GenerateFields(context, rootPath, replacements: fixedListSnapshotField.Replacements);
            fixedListGenerator.GenerateMasks(context, 2, false, fieldIndex);
            fixedListGenerator.AppendTarget(parentContainer);
        }

        private static bool GenerateCompositeField(Context context, TypeInformation type, ComponentSerializer parentContainer,
            TypeTemplate template, string rootPath)
        {
            var compositeGenerator = new ComponentSerializer(context, type, template);
            // 查找并应用 Composite Override，处理 Composite 字段时跳过已覆盖的片段
            var overrides = compositeGenerator.GenerateCompositeOverrides(context, rootPath);
            if (overrides != null)
                compositeGenerator.AppendTarget(parentContainer);
            var fieldIt = 0;
            // 验证所有待生成字段均为基础类型这一前提
            if (compositeGenerator.TypeInformation.GhostFields.Count > 0)
            {
                var areAllPrimitive = type.GhostFields.TrueForAll(f => f.Kind == GenTypeKind.Primitive);
                var field = type.GhostFields[0];
                if (!areAllPrimitive)
                {
                    context.diagnostic.LogError(
                        $"Can't generate a composite serializer for {type.Description}. The struct fields must be all primitive types but are {field.TypeFullName}!",
                        type.Location);
                    return false;
                }
                var areAllSameType = type.GhostFields.TrueForAll(f => f.FieldTypeName == field.FieldTypeName);
                if (!areAllSameType)
                {
                    context.diagnostic.LogError($"Can't generate a composite serializer for {type.Description}. The struct fields must be all of the same type! " +
                                                $"Check the template assignment in your UserDefinedTemplate class implementation. " +
                                                $"Composite templates should be used only for generating types that has all the same fields (i.e float3)", type.Location);
                    return false;
                }
            }
            foreach (var childGhostField in compositeGenerator.TypeInformation.GhostFields)
            {
                // Composite Template 会强制聚合 ChangeMask，按当前设计无法通过 GhostField.Composite 标志覆盖该行为
                // 结合其当前工作方式，实际上只能支持基础字段类型，限制很大
                // 因此目前这里只支持始终生成 1 位 ChangeMask 的 Template
                // TODO: 基于这些限制，可以考虑从 Template 中移除 Composite 概念
                if (!TryGetTypeTemplate(type, context, out var fieldTemplate))
                {
                    context.diagnostic.LogError(
                        $"Inside type '{context.generatorName}', we could not find the exact template for field '{type.FieldName}' with configuration '{type.Description}', which means that netcode cannot serialize this type (with this configuration), as it does not know how. " +
                        $"To rectify, either a) define your own template for this type (and configuration), b) resolve any other code-gen errors, or c) modify your GhostField(...) configuration (Quantization, SubType, SmoothingAction etc) to use a known, already existing template. Known templates are {context.templateProvider.FormatAllKnownTypes()}. All known subTypes are {context.templateProvider.FormatAllKnownSubTypes()}!",
                        type.Location);
                    context.diagnostic.LogError(
                        $"Unable to generate serializer for GhostField '{type.TypeFullName}.{childGhostField.TypeFullName}.{childGhostField.TypeFullName}' (description: {childGhostField.Description}) while building the composite!",
                        type.Location);
                }
                var g = new ComponentSerializer(context, childGhostField, fieldTemplate);
                g.GenerateFields(context, rootPath, overrides);
                g.GenerateMasks(context, 1, true, fieldIt);
                g.AppendTarget(parentContainer);
                ++fieldIt;
            }
            return true;
        }

        #endregion

        public struct GeneratedFile
        {
            public string GeneratedFileName;
            public string Code;
        }

        public class CodeGenCache
        {
            private Dictionary<string, GhostCodeGen> cache;
            private TemplateRegistry provider;
            private Context context;

            public CodeGenCache(TemplateRegistry templateRegistry, Context context)
            {
                this.provider = templateRegistry;
                this.context = context;
                this.cache = new Dictionary<string, GhostCodeGen>(128);
            }

            public GhostCodeGen GetTemplate(string templatePath)
            {
                if (!cache.TryGetValue(templatePath, out var codeGen))
                {
                    var templateData = provider.GetTemplateData(templatePath);
                    codeGen = new GhostCodeGen(templatePath, templateData, context);
                    cache.Add(templatePath, codeGen);
                }
                return codeGen;
            }

            public GhostCodeGen GetTemplateWithOverride(string templatePath, string templateOverride)
            {
                var key = templatePath + templateOverride;
                if (!cache.TryGetValue(key, out var codeGen))
                {
                    var templateData = provider.GetTemplateData(templatePath);
                    codeGen = new GhostCodeGen(templatePath, templateData, context);
                    if (!string.IsNullOrEmpty(templateOverride))
                    {
                        var overrideTemplateData = provider.GetTemplateData(templateOverride);
                        var codeGenOverride = new GhostCodeGen(templateOverride, overrideTemplateData, context);
                        foreach (var f in codeGenOverride.Fragments)
                            codeGen.Fragments[f.Key].Template = f.Value.Template;
                    }
                    cache.Add(key, codeGen);
                }
                return codeGen;
            }
        }

        // 包含当前序列化过程的全部状态
        // Generator 必须无状态且不可变，只有 Context 可以包含可变数据
        public class Context
        {
            internal GeneratorExecutionContext executionContext;
            public string rootNs;
            public string generatedNs;
            public readonly TemplateRegistry templateProvider;
            public readonly IDiagnosticReporter diagnostic;
            public readonly CodeGenCache codeGenCache;
            public readonly List<GeneratedFile> batch;
            public readonly List<TypeInformation> types;
            public readonly HashSet<string> imports;
            public readonly HashSet<string> generatedGhosts;
            public readonly HashSet<string> generatedTypes;
            public readonly HashSet<string> generatedSerializers;
            public struct SerializationStrategyCodeGen
            {
                public TypeInformation TypeInfo;
                public string DisplayName;
                public string ComponentTypeName;
                public string VariantTypeName;
                public string Hash;
                public bool IsSerialized;
                public GhostComponentAttribute GhostAttribute;
                public string InputBufferComponentTypeName;

            }
            public readonly List<SerializationStrategyCodeGen> serializationStrategies;

            // 遵循 Roslyn 的内部类命名约定，即 Namespace.ClassName[+DeclaringClass]+Class
            public string variantTypeFullName;
            public ulong variantHash;
            public string generatorName;
            public string generatedFilePrefix;
            // ChangeMask 的总位数
            public int changeMaskBitCount;
            // 当前已使用的 Mask 位数
            public int curChangeMaskBits;
            public ulong ghostFieldHash;
            public TypeInformation root;

            struct FieldState
            {
                public int changeMaskBitCount;
                public int curChangeMaskBits;
                public string generatorName;
                public string generatedFilePrefix;
                public string generatedNs;
            }

            private Stack<FieldState> m_FieldStateStack = new Stack<FieldState>();

            public void PushState()
            {
                m_FieldStateStack.Push(new FieldState
                {
                    changeMaskBitCount = changeMaskBitCount,
                    curChangeMaskBits =  curChangeMaskBits,
                    generatorName =  generatorName,
                    generatedFilePrefix =  generatedFilePrefix,
                    generatedNs =  generatedNs,
                });
            }
            public void PopState()
            {
                var state = m_FieldStateStack.Pop();
                changeMaskBitCount = state.changeMaskBitCount;
                curChangeMaskBits =  state.curChangeMaskBits;
                generatorName =  state.generatorName;
                generatedFilePrefix =  state.generatedFilePrefix;
                generatedNs =  state.generatedNs;
            }
            public void ResetState()
            {
                m_FieldStateStack.Clear();
                changeMaskBitCount = 0;
                curChangeMaskBits = 0;
                ghostFieldHash = 0;
                variantTypeFullName = null;
                variantHash = 0;
                imports.Clear();
                imports.Add("Unity.Entities");
                imports.Add("Unity.Collections");
                imports.Add("Unity.NetCode");
                imports.Add("Unity.Transforms");
                imports.Add("Unity.Mathematics");
            }

            string GenerateNamespaceFromAssemblyName(string assemblyName)
            {
                return $"{Regex.Replace(assemblyName, @"[^\w\.]", "_", RegexOptions.Singleline)}.Generated";
            }

            public Context(TemplateRegistry templateRegistry,
                IDiagnosticReporter reporter, GeneratorExecutionContext context, string assemblyName)
            {
                executionContext = context;
                types = new List<TypeInformation>(16);
                serializationStrategies = new List<SerializationStrategyCodeGen>(32);
                templateProvider = templateRegistry;
                codeGenCache = new CodeGenCache(templateRegistry, this);
                batch = new List<GeneratedFile>(256);
                imports = new HashSet<string>();
                generatedGhosts = new HashSet<string>();
                generatedTypes = new HashSet<string>();
                generatedSerializers = new HashSet<string>();
                diagnostic = reporter;
                rootNs = GenerateNamespaceFromAssemblyName(assemblyName);
                generatedNs = null;
                ResetState();
            }
        }
    }
}
