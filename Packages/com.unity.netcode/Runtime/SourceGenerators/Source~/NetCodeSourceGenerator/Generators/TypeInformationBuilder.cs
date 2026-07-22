using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Unity.NetCode.Roslyn;

namespace Unity.NetCode.Generators
{
    /// <summary>
    /// 用于从 Roslyn ITypeSymbol 构建 TypeInformation 树的辅助构建器
    /// </summary>
    internal struct TypeInformationBuilder
    {
        public enum SerializationMode
        {
            Component,
            Commands,
            Variant,
        }

        private GeneratorExecutionContext m_context;
        private IDiagnosticReporter m_Reporter;
        private SerializationMode m_SerializationMode;
        private List<string> m_MissingGhostFields;

        public List<string> MissingGhostFields => m_MissingGhostFields;

        /// <summary>
        /// 控制参与序列化的结构体成员所需访问级别
        /// Component 与 Buffer 只能在 public 成员上声明 GhostField
        /// Variant 声明无需遵循该限制，因为 Variant 只是代理类型，不会在运行时直接使用
        /// </summary>
        private bool m_RequiresPublicFields;

        /// <summary>
        /// 限制 FixedList 允许的元素数量，主要有两个原因：
        /// - 简化序列化与反序列化代码，通用版本会增加不必要的复杂度
        /// - 减少对 Snapshot 数据位掩码的影响，避免发送过多位
        /// FixedList 的常见用途是在 Component 内保存小型列表，而且其 Chunk 占用本就较高
        /// 因此该限制通常不会对用户造成明显影响
        ///
        /// 当给定元素类型使 FixedList 可容纳超过 64 个元素时，报告问题并限制元素数量
        /// 这并不理想，但有时为达到所需元素数量必须选用稍大的 FixedList 容量
        /// 因而不能直接阻止编译
        ///
        /// RPC 与 Snapshot 使用不同容量限制，为 RPC 保留额外灵活性
        /// 当前上限为：
        /// RPC：1024
        /// 其他类型：64
        /// </summary>
        private int m_FixedListSizeCap = 0;

        public TypeInformationBuilder(IDiagnosticReporter reporter, GeneratorExecutionContext context, SerializationMode mode)
        {
            m_context = context;
            m_Reporter = reporter;
            m_SerializationMode = mode;
            m_MissingGhostFields = new List<string>();
            m_RequiresPublicFields = mode != SerializationMode.Variant;
        }

        /// <summary>
        /// 为 <paramref name="symbol"/> 类型构建代码生成专用的语义树模型
        /// </summary>
        /// <returns>构建成功时返回类型信息，否则返回 null</returns>
        public TypeInformation BuildTypeInformation(ITypeSymbol symbol, GhostComponentAttribute ghostAttribute, GhostField ghostFieldOverride = null)
        {
            m_context.CancellationToken.ThrowIfCancellationRequested();
            m_Reporter.LogDebug($"Building type info for {symbol}");
            var isEnableableComponent = Roslyn.Extensions.ImplementsInterface(symbol, "Unity.Entities.IEnableableComponent");
            var hasGhostEnabledBitAttribute = Roslyn.Extensions.GetAttribute(symbol, "Unity.NetCode", "GhostEnabledBitAttribute") != null;
            var fullTypeName = Roslyn.Extensions.GetFullTypeName(symbol);

            if (hasGhostEnabledBitAttribute && !isEnableableComponent)
            {
                m_Reporter.LogError($"'{fullTypeName}' has attribute `[GhostEnabledBit]` (denoting that its enabled bit will be replicated), but the component is not implementing the `IEnableableComponent` interface! Either remove the attribute, or implement the interface.");
                return null;
            }

            var typeInfo = new TypeInformation
            {
                Kind = Roslyn.Extensions.GetTypeKind(symbol),
                ComponentType = Roslyn.Extensions.GetComponentType(symbol),
                TypeFullName = fullTypeName,
                Namespace = Roslyn.Extensions.GetFullyQualifiedNamespace(symbol),
                FieldName = string.Empty,
                FieldTypeName = Roslyn.Extensions.GetFieldTypeName(symbol),
                UnderlyingTypeName = String.Empty,
                Attribute = TypeAttribute.Empty(),
                AttributeMask = m_SerializationMode != SerializationMode.Commands
                    ? TypeAttribute.AttributeFlags.All
                    : TypeAttribute.AttributeFlags.None,
                GhostAttribute = ghostAttribute,
                Location = symbol.Locations[0],
                Symbol = symbol,
                ShouldSerializeEnabledBit = isEnableableComponent && hasGhostEnabledBitAttribute,
                HasDontSupportPrefabOverridesAttribute = Roslyn.Extensions.GetAttribute(symbol, "Unity.NetCode", "DontSupportPrefabOverridesAttribute") != null,
                IsTestVariant = false,
            };
            // 屏蔽不适用的继承特性；SubType 永不继承，Buffer 字段也不参与插值
            if (typeInfo.ComponentType != ComponentType.Component)
                typeInfo.AttributeMask &= ~TypeAttribute.AttributeFlags.InterpolatedAndExtrapolated;

            // Command 与类似 Component 的数据都限制容量，只有 RPC 使用更高上限
            if (typeInfo.ComponentType == ComponentType.Rpc)
                m_FixedListSizeCap = 1024;
            else
                m_FixedListSizeCap = 64;

            // 获取成员有时开销较高，可能达到数十毫秒
            var members = symbol.GetMembers();
            using (new Profiler.Auto("ParseMembers"))
            {
                foreach (var member in members.OfType<IFieldSymbol>())
                {
                    m_context.CancellationToken.ThrowIfCancellationRequested();
                    if (typeInfo.ComponentType is ComponentType.CommandData or ComponentType.Rpc &&
                        m_SerializationMode == SerializationMode.Commands &&
                        ShouldDiscardCommandField(member))
                        continue;

                    // 该操作开销较高，可能达到数毫秒
                    var memberType = member.Type;
                    var field = ParseFieldType(member, memberType, typeInfo, string.Empty, 1, ghostFieldOverride);
                    if (field != null)
                        typeInfo.GhostFields.Add(field);
                }
            }

            using (new Profiler.Auto("ParseProperties"))
            {
                foreach (var prop in members.OfType<IPropertySymbol>())
                {
                    m_context.CancellationToken.ThrowIfCancellationRequested();
                    if (!CheckIsSerializableProperty(prop))
                        continue;

                    if (typeInfo.ComponentType is ComponentType.CommandData or ComponentType.Rpc &&
                        m_SerializationMode == SerializationMode.Commands &&
                        ShouldDiscardCommandField(prop))
                        continue;

                    var field = ParseFieldType(prop, prop.Type, typeInfo, string.Empty, 1, ghostFieldOverride);
                    if (field != null)
                        typeInfo.GhostFields.Add(field);
                }
            }

            return typeInfo;
        }

        /// <summary>
        /// 为 Variant 类型 <paramref name="variantSymbol"/> 构建 TypeInformation 树模型
        /// </summary>
        /// <returns>构建成功时返回 Variant 类型信息，否则返回 null</returns>
        public TypeInformation BuildVariantTypeInformation(ITypeSymbol variantSymbol, AttributeData variantAttribute, GhostComponentAttribute ghostAttribute)
        {
            m_context.CancellationToken.ThrowIfCancellationRequested();
            // 从 Template 声明获取参数，它是需要注入序列化代码的目标类型
            if (variantAttribute.ConstructorArguments.Length == 0)
            {
                m_Reporter.LogError($"{variantSymbol.Name} does not have constructor arguments", variantSymbol.Locations[0]);
                return null;
            }

            var adapteeType = (ITypeSymbol)variantAttribute.ConstructorArguments[0].Value;
            if (adapteeType == null)
            {
                m_Reporter.LogError($"{variantSymbol} constructed with a null type", variantSymbol.Locations[0]);
                return null;
            }
            if (adapteeType.DeclaredAccessibility == Accessibility.NotApplicable)
            {
                m_Reporter.LogError($"{variantSymbol.Name}: problem parsing this type, make sure the compilation unit compiles", variantSymbol.Locations[0]);
                return null;
            }
            if (adapteeType.DeclaredAccessibility != Accessibility.Public)
            {
                m_Reporter.LogError($"{variantSymbol.Name}: the component type must be public accessible", variantSymbol.Locations[0]);
                return null;
            }
            if (Roslyn.Extensions.GetAttribute(adapteeType, "Unity.NetCode", "DontSupportPrefabOverridesAttribute") != null)
            {
                m_Reporter.LogError($"{variantSymbol.Name}: the target component does not support variation because it has the DontSupportPrefabOverridesAttribute", variantSymbol.Locations[0]);
                return null;
            }
            var adapteeComponentType = Roslyn.Extensions.GetComponentType(adapteeType);
            if (adapteeComponentType != ComponentType.Component && adapteeComponentType != ComponentType.Buffer &&
                !Roslyn.Extensions.InheritsFromBase(adapteeType, "UnityEngine.Component"))
            {
                m_Reporter.LogError($"{variantSymbol.Name}: the component type must be IComponentData, IBufferElementData or UnityEngine.Component", variantSymbol.Locations[0]);
                return null;
            }

            // TODO: 为该参数解析逻辑补充测试
            var isTestVariant = false;
            if (variantAttribute.ConstructorArguments.Length == 2)
            {
                // 第二个参数可能是 bool
                if (variantAttribute.ConstructorArguments[1].Value is bool testVariant)
                {
                    isTestVariant = testVariant;
                }
                // 否则假定它是显示名称字符串
            }
            else if (variantAttribute.ConstructorArguments.Length == 3)
            {
                if (variantAttribute.ConstructorArguments[2].Value is bool testVariant)
                {
                    isTestVariant = testVariant;
                }
                else
                {
                    m_Reporter.LogError($"{variantSymbol.Name}: `variantAttribute.ConstructorArguments[2]` is somehow not a bool, but expected it to be `IsTestVariant`.");
                    return null;
                }
            }

            // 验证并收集成员：遍历 Variant 声明中的字段，只接受原 Component 中同样存在的字段
            // 任何 private 或缺失字段都视为错误
            var declaredMembers = new List<ValueTuple<ISymbol, ITypeSymbol>>(32);
            bool hasErrors = false;
            using (new Profiler.Auto("ValidationAndExtraction"))
            {
                // 该检查理应由 NetCode Roslyn Analyzer 在 IDE 编辑阶段完成
                // 但为保证健壮性，Source Generator 仍需再次检查
                foreach (var member in variantSymbol.GetMembers().OfType<IFieldSymbol>())
                {
                    if(member.DeclaredAccessibility != Accessibility.Public && member.IsImplicitlyDeclared ||
                       member.Name.EndsWith("k__BackingField"))
                        continue;

                    var originalMember = adapteeType.GetMembers(member.Name).FirstOrDefault();
                    if(originalMember == null ||
                       (originalMember as IFieldSymbol)?.Type.GetFullTypeName() != member.Type.GetFullTypeName())
                    {
                        hasErrors = true;
                        m_Reporter.LogError($"{variantSymbol.Name}: Cannot find member {member.Name} type: {member.Type.Name} in {adapteeType.Name}", member.Locations[0]);
                        continue;
                    }
                    if (originalMember.DeclaredAccessibility != Accessibility.Public)
                    {
                        hasErrors = true;
                        m_Reporter.LogError($"{variantSymbol.Name}: member {member.Name} type: {member.Type.Name} in {adapteeType.Name} must be public", member.Locations[0]);
                        continue;
                    }
                    declaredMembers.Add((member, member.Type));
                }
                foreach (var prop in variantSymbol.GetMembers().OfType<IPropertySymbol>())
                {
                    if (!CheckIsSerializableProperty(prop))
                        continue;

                    var originalMember = adapteeType.GetMembers(prop.Name).FirstOrDefault();
                    if(originalMember == null ||
                       (originalMember as IPropertySymbol)?.Type.GetFullTypeName() != prop.Type.GetFullTypeName())
                    {
                        hasErrors = true;
                        m_Reporter.LogError($"{variantSymbol.Name}: Cannot find property {prop.Name} type: {prop.Type.Name} in {adapteeType.Name}", prop.Locations[0]);
                        continue;
                    }
                    if (originalMember.DeclaredAccessibility != Accessibility.Public)
                    {
                        hasErrors = true;
                        m_Reporter.LogError($"{variantSymbol.Name}: property {prop.Name} type: {prop.Type.Name} in {adapteeType.Name} must be public", prop.Locations[0]);
                        continue;
                    }
                    declaredMembers.Add((prop, prop.Type));
                }
            }
            // 存在错误时直接跳过更安全
            if (hasErrors)
                return null;

            m_context.CancellationToken.ThrowIfCancellationRequested();
            var fullTypeName = Roslyn.Extensions.GetFullTypeName(adapteeType);
            var hasGhostEnabledBitAttribute = Roslyn.Extensions.GetAttribute(variantSymbol, "Unity.NetCode", "GhostEnabledBitAttribute") != null;
            var adapteeIsEnableableComponent = Roslyn.Extensions.ImplementsInterface(adapteeType, "Unity.Entities.IEnableableComponent");

            // TODO: 为 Variant 上的 `[GhostEnabledBit]` 补充测试
            if (hasGhostEnabledBitAttribute && !adapteeIsEnableableComponent)
            {
                m_Reporter.LogError($"'{fullTypeName}' (a variant) has attribute `[GhostEnabledBit]` (denoting that we intend to replicate the enabled bit on the source type), but the source type (`{variantSymbol.Name}`) is not implementing the `IEnableableComponent` interface! Either remove the attribute from the variant, or implement the interface.");
                return null;
            }

            var typeInfo = new TypeInformation
            {
                Kind = Roslyn.Extensions.GetTypeKind(adapteeType),
                ComponentType = adapteeComponentType,
                TypeFullName = fullTypeName,
                UnderlyingTypeName = String.Empty,
                Namespace = Roslyn.Extensions.GetFullyQualifiedNamespace(adapteeType),
                FieldName = string.Empty,
                FieldTypeName = Roslyn.Extensions.GetFieldTypeName(adapteeType),
                Attribute = TypeAttribute.Empty(),
                GhostAttribute = ghostAttribute,
                Location = variantSymbol.Locations[0],
                Symbol = variantSymbol,
                IsTestVariant = isTestVariant,
                ShouldSerializeEnabledBit = adapteeIsEnableableComponent && hasGhostEnabledBitAttribute,
            };

            // 屏蔽不适用的继承特性；SubType 永不继承，Buffer 字段也不参与插值
            if (typeInfo.ComponentType != ComponentType.Component)
                typeInfo.AttributeMask &= ~TypeAttribute.AttributeFlags.Interpolated;

            using (new Profiler.Auto("ParseMembers"))
            {
                foreach (var member in declaredMembers)
                {
                    m_Reporter.LogDebug($"Parsing field {member}");
                    var field = ParseFieldType(member.Item1, member.Item2, typeInfo, string.Empty);
                    if (field != null)
                        typeInfo.GhostFields.Add(field);
                }
            }
            return typeInfo;
        }

        /// <summary>
        /// 如果字段 <paramref name="member"/> 应参与序列化，则为其构建 TypeInformation 树
        /// 结构体成员必须满足以下条件：
        /// - 成员必须为 public
        /// - 成员不能为 static
        /// - 成员必须具有 [GhostField] 标记或自定义 Ghost Override
        /// - 成员类型必须是受支持的 Primitive、Enum 或 Struct，类成员无效
        /// 该方法会递归处理嵌套成员
        /// </summary>
        /// <returns>成员满足全部要求时返回有效 TypeInformation，否则返回 null</returns>
        public TypeInformation ParseFieldType(ISymbol member, ITypeSymbol memberType, TypeInformation parent, string fieldPath, int level=1, GhostField ghostFieldOverride = null)
        {
            m_context.CancellationToken.ThrowIfCancellationRequested();
            var ghostField = default(GhostField);
            if (m_SerializationMode == SerializationMode.Component)
            {
                if (ghostFieldOverride != null)
                {
                    // 仅对可作为 Ghost Field 的有效成员应用 Override
                    if (!member.IsStatic && member.DeclaredAccessibility == Accessibility.Public)
                        ghostField = ghostFieldOverride;
                }
                else
                    ghostField = TryGetGhostField(member);
            }
            if(m_SerializationMode != SerializationMode.Commands)
            {
                if (member.IsStatic || (m_RequiresPublicFields && member.DeclaredAccessibility != Accessibility.Public))
                {
                    if(ghostField != null)
                        m_Reporter.LogError($"GhostField present on a non public or non instance field '{parent.TypeFullName}.{member.Name}'! GhostFields must be public, instance fields.");
                    return null;
                }

                // 跳过没有 [GhostField] 特性或 SendData 为 false 的字段
                if ((ghostField == null && level == 1))
                {
                    // Buffer 需要进一步验证，因此在此收集所有缺少 GhostField 的字段
                    if ((parent.ComponentType == ComponentType.Buffer || parent.ComponentType == ComponentType.CommandData))
                    {
                        m_MissingGhostFields.Add($"{parent.TypeFullName}.{member.Name}");
                    }
                    return null;
                }
                if ((ghostField != null && !ghostField.SendData))
                    return null;
            }
            else if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public)
                return null;

            if(member.Name.StartsWith("__COMMAND", StringComparison.Ordinal) ||
               member.Name.StartsWith("__GHOST", StringComparison.Ordinal))
            {
                m_Reporter.LogError($"Invalid field name '{parent.TypeFullName}.{member.Name}'. __GHOST and __COMMAND are reserved prefixes and cannot be used in namespace, type and field names!",
                    member.Locations[0]);
                return null;
            }

            GenTypeKind typeKind;
            if (member is IFieldSymbol && ((IFieldSymbol)member).IsFixedSizeBuffer)
            {
                typeKind = GenTypeKind.FixedSizeArray;
            }
            else
            {
                typeKind = Roslyn.Extensions.GetTypeKind(memberType);
                if (typeKind == GenTypeKind.Struct && Roslyn.Extensions.IsFixedList(memberType))
                {
                    typeKind = GenTypeKind.FixedList;
                }
                if (typeKind == GenTypeKind.Invalid)
                {
                    m_Reporter.LogError($"GhostField annotation present on non serializable field '{parent.TypeFullName}.{member.Name}'.");
                    return null;
                }

                if (typeKind != GenTypeKind.Struct && (ghostField != null && ghostField.Composite.HasValue && ghostField.Composite.Value))
                    m_Reporter.LogError($"GhostField for field '{parent.TypeFullName}.{member.Name}' set Composite=True, but this is invalid on primitive types.");
            }

            // 补充验证并跳过无关字段
            var typeInfo = new TypeInformation
            {
                Kind = typeKind,
                TypeFullName = Roslyn.Extensions.GetFullTypeName(memberType),
                Namespace = Roslyn.Extensions.GetFullyQualifiedNamespace(memberType),
                FieldName = parent.Kind != GenTypeKind.FixedList ? member.Name : string.Empty,
                UnderlyingTypeName = Roslyn.Extensions.GetUnderlyingTypeName(memberType),
                ContainingTypeFullName = Roslyn.Extensions.GetFullTypeName(member.ContainingType),
                FieldTypeName = Roslyn.Extensions.GetFieldTypeName(memberType),
                Attribute = parent.Attribute,
                AttributeMask = parent.AttributeMask,
                FieldPath = fieldPath,
                Location = member.Locations[0],
                CanBatchPredict = CanBatchPredict(member),
                Symbol = member as ITypeSymbol
            };

            // 始终重置 SubType，因为它不会继承
            typeInfo.Attribute.subtype = 0;
            // 存在 GhostField 配置时读取子字段特性
            if (ghostField != null)
            {
                if (ghostField.Quantization >= 0) typeInfo.Attribute.quantization = ghostField.Quantization;
                if (ghostField.Smoothing > 0) typeInfo.Attribute.smoothing = (uint)ghostField.Smoothing;
                if (ghostField.SubType != 0) typeInfo.Attribute.subtype = ghostField.SubType;
                // 按继承规则，子字段特性的优先级更高
                // Composite 的继承规则如下：
                //  子级/父级     N/A    False   True
                //    N/A        false   false   true
                //    False      false   false   true
                //    True       true    true    true
                if (ghostField.Composite.HasValue && !typeInfo.Attribute.aggregateChangeMask)
                {
                    typeInfo.Attribute.aggregateChangeMask = ghostField.Composite.Value;
                    if (typeKind != GenTypeKind.Struct && typeInfo.Attribute.aggregateChangeMask)
                    {
                        m_Reporter.LogInfo($"GhostField composite set to true for primitive field '{fieldPath} {parent.TypeFullName}.{member.Name}', which will be ignored. We assume this is fine as the parent having Composite is valid.");
                        typeInfo.Attribute.aggregateChangeMask = false;
                    }
                }

                if (ghostField.MaxSmoothingDistance > 0) typeInfo.Attribute.maxSmoothingDist = ghostField.MaxSmoothingDistance;
            }

            // 整数类型不支持 Quantization 或插值标志
            if (typeKind == GenTypeKind.Primitive && Roslyn.Extensions.IsIntegerType(memberType))
                typeInfo.AttributeMask &= ~(TypeAttribute.AttributeFlags.InterpolatedAndExtrapolated|TypeAttribute.AttributeFlags.Interpolated|TypeAttribute.AttributeFlags.Quantized);

            // 再根据掩码重置不允许继承的配置
            typeInfo.Attribute.smoothing &= (uint)(typeInfo.AttributeMask & TypeAttribute.AttributeFlags.InterpolatedAndExtrapolated);
            typeInfo.Attribute.aggregateChangeMask &= (typeInfo.AttributeMask & TypeAttribute.AttributeFlags.Composite) != 0;

            if((typeInfo.AttributeMask & TypeAttribute.AttributeFlags.Quantized) == 0)
                typeInfo.Attribute.quantization = -1;

            if (typeKind == GenTypeKind.FixedSizeArray)
            {
                var elementPointedType = ((IPointerTypeSymbol)memberType).PointedAtType;
                typeInfo.ElementCount = ((IFieldSymbol)member).FixedSize;
                var elementType = new TypeInformation
                {
                    Kind = Extensions.GetTypeKind(elementPointedType),
                    TypeFullName = Roslyn.Extensions.GetFullTypeName(elementPointedType),
                    Namespace = Roslyn.Extensions.GetFullyQualifiedNamespace(elementPointedType),
                    FieldName = null,
                    UnderlyingTypeName = Roslyn.Extensions.GetUnderlyingTypeName(elementPointedType),
                    ContainingTypeFullName = Roslyn.Extensions.GetFullTypeName(elementPointedType.ContainingType),
                    FieldTypeName = Roslyn.Extensions.GetFieldTypeName(elementPointedType),
                    Attribute = parent.Attribute,
                    AttributeMask = parent.AttributeMask,
                    FieldPath = fieldPath,
                    Location = member.Locations[0],
                    CanBatchPredict = CanBatchPredict(member),
                    Symbol = member as ITypeSymbol
                };
                typeInfo.PointeeType = elementType;
                return typeInfo;
            };

            if (typeKind == GenTypeKind.FixedList)
            {
                var fixedListArgumentType = ((INamedTypeSymbol)memberType).TypeArguments[0];
                // 强制将 aggregateChangeMask 设为 false
                // FixedList 永远不会与同一结构体中的其他字段聚合，并且始终使用 2 位 ChangeMask
                typeInfo.Attribute.aggregateChangeMask = false;
                var argumentTypeInfo = ParseFieldType(fixedListArgumentType, fixedListArgumentType, typeInfo, null, level+1);
                typeInfo.ElementCount = FixedListUtils.CalculateNumElements(memberType);
                var customCapacityCap = 0;
                customCapacityCap = TryGetGhostFixedListCapacity(member);
                if (customCapacityCap > m_FixedListSizeCap)
                {
                    m_Reporter.LogError($"Invalid GhostFixedListCapacity attribute present on {member.ToDisplayString()} of type {memberType.ToDisplayString()}. The maximum allowed capacity for a fixed list must bet less or equal than {m_FixedListSizeCap} elements.");
                    customCapacityCap = 0;
                }
                // 大于 0 表示特性存在且有效，可以直接应用该上限
                if(customCapacityCap > 0)
                    typeInfo.ElementCount = customCapacityCap;
                // 否则当 ElementCount 超过复制类型允许的默认最大容量时报告错误
                // 当前上限为：
                // - RPC：1024
                // - 其他类型：64
                if (typeInfo.ElementCount > m_FixedListSizeCap)
                {
                    m_Reporter.LogError($"{member.ToDisplayString()} of type {memberType.ToDisplayString()} has a capacity greater than {m_FixedListSizeCap} elements. Replicated fixed lists can contain at most {m_FixedListSizeCap} elements. If the capacity exceed, please use the GhostFixedListCapacity attribute to constrain the maximum allowed length of the list.");
                    typeInfo.ElementCount = m_FixedListSizeCap;
                }
                typeInfo.GenericTypeName = Roslyn.Extensions.GetGenericTypeName(memberType);
                typeInfo.PointeeType = argumentTypeInfo;
                return typeInfo;
            }

            if (parent.Kind == GenTypeKind.FixedList)
            {
                fieldPath = string.Empty;
                typeInfo.FieldPath = string.Empty;
            }
            if (typeKind != GenTypeKind.Struct)
                return typeInfo;

            var members = memberType.GetMembers();

            foreach (var f in members.OfType<IFieldSymbol>())
            {
                var path = string.IsNullOrEmpty(fieldPath)
                    ? typeInfo.FieldName
                    : string.Concat(fieldPath, ".", typeInfo.FieldName);

                var field = ParseFieldType(f, f.Type, typeInfo, path,level + 1);
                if (field != null)
                    typeInfo.GhostFields.Add(field);
            }

            // 支持字段成员上的 Property，但存在限制：只能返回基础类型或明确允许的结构体
            // - 无法充分控制给定成员会暴露哪些 Property，例如不应序列化 float3 的 xyz 等 Swizzle 组合
            // - this[int index] 之类的成员或返回自身类型的 Property 可能造成递归
            // - 不支持 this[] 形式的索引器 Property
            foreach (var prop in members.OfType<IPropertySymbol>())
            {
                if (SymbolEqualityComparer.Default.Equals(memberType,prop.Type) || !CheckIsSerializableProperty(prop))
                    continue;

                var path = string.IsNullOrEmpty(fieldPath)
                    ? typeInfo.FieldName
                    : string.Concat(fieldPath, ".", member.Name);

                var field = ParseFieldType(prop, prop.Type, typeInfo, path, level + 1);
                if (field != null)
                    typeInfo.GhostFields.Add(field);
            }
            return typeInfo;
        }

        private bool CheckIsSerializableProperty(IPropertySymbol f)
        {
            string GetErrorReason()
            {
                // 排除所有索引器形式的访问器
                if (f.IsIndexer)
                    return "it is an indexer like property.";
                if (f.GetMethod == null)
                    return "it does not have any getter. Both setter and getters are required.";
                if (f.GetMethod.DeclaredAccessibility != Accessibility.Public || f.GetMethod.IsStatic)
                    return "the setter is not public.";
                if (f.SetMethod == null)
                    return "does not have setter. Both setter and getters are required.";
                if (f.SetMethod.DeclaredAccessibility != Accessibility.Public || f.SetMethod.IsStatic)
                    return "the setter is not public.";
                // 仅支持返回基础类型或明确允许结构体的非索引器 Property
                var typeKind = Roslyn.Extensions.GetTypeKind(f.GetMethod.ReturnType);
                if (typeKind == GenTypeKind.Invalid)
                    return $"it return an unknown or non serializable type {f.GetMethod.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}";
                if (typeKind != GenTypeKind.Struct)
                    return null;
                // 以下返回类型是允许序列化的结构体 Property 例外
                var returnTypeFullTypename = Roslyn.Extensions.GetFullTypeName(f.GetMethod.ReturnType);
                switch (returnTypeFullTypename)
                {
                    case "Unity.NetCode.NetworkTick": return null;
                    case "Unity.Mathematics.float3": return null;
                    case "Unity.Mathematics.float2": return null;
                    case "Unity.Mathematics.float4": return null;
                    case "Unity.Mathematics.quaternion": return null;
                    default:
                        return $"it returns a non primitive type {returnTypeFullTypename}. " +
                               $"Properties can be serialized if they return one of the following types: Unity.NetCode.NetworkTick, Unity.Mathematics.float3, Unity.Mathematics.float2, Unity.Mathematics.float4, Unity.Mathematics.quaternion";
                }
            }

            // 不接受 float3 与 float4 的任何 Swizzle Property
            if(f.ContainingType.Name == "float3" || f.ContainingType.Name == "float4")
                return false;

            var errorReason = GetErrorReason();
            var isValid = string.IsNullOrEmpty(errorReason);
            if (!isValid)
            {
                var ghostField = TryGetGhostField(f);
                if (ghostField != null && ghostField.SendData)
                {
                    m_Reporter.LogError($"It is not possible to serialize property {f.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)} because {errorReason}");
                }
            }
            return isValid;
        }

        private bool ShouldDiscardCommandField(ISymbol symbol)
        {
            var attribute = Roslyn.Extensions.GetAttribute(symbol, "Unity.NetCode", "DontSerializeForCommandAttribute");
            if (attribute != null)
                return true;
            // 接口成员检查只可能适用于 Property，不应对字段执行
            if (symbol is IPropertySymbol)
            {
                foreach (var iface in symbol.ContainingType.Interfaces)
                {
                    var member = iface.GetMembers(symbol.Name);
                    if (member == null || member.Length == 0)
                        continue;
                    if(Roslyn.Extensions.GetAttribute(member[0], "Unity.NetCode", "DontSerializeForCommandAttribute") != null)
                        return true;
                }
            }
            return false;
        }


        /// <summary>
        /// 检查给定字段 <paramref name="fieldSymbol"/> 是否具有 GhostFieldAttribute
        /// </summary>
        /// <returns>
        /// 存在标记或 Override 时返回有效 GhostField，否则返回 null
        /// </returns>
        private GhostField TryGetGhostField(ISymbol fieldSymbol)
        {
            var ghostField = Roslyn.Extensions.GetAttribute(fieldSymbol, "Unity.NetCode", "GhostFieldAttribute");
            if (ghostField == null)
                ghostField = Roslyn.Extensions.GetAttribute(fieldSymbol, "", "GhostField");
            if (ghostField != null)
            {
                var fieldDescriptor = new GhostField();
                if (ghostField.NamedArguments.Length > 0)
                    foreach (var a in ghostField.NamedArguments)
                    {
                        typeof(GhostField).GetProperty(a.Key)?.SetValue(fieldDescriptor, a.Value.Value);
                    }

                return fieldDescriptor;
            }

            return default;
        }
        /// <summary>
        /// 检查给定字段 <paramref name="fieldSymbol"/> 是否具有 GhostFixedListCapacityAttribute
        /// </summary>
        /// <returns>
        /// 存在该特性时返回配置的容量，否则返回 0
        /// </returns>
        private int TryGetGhostFixedListCapacity(ISymbol fieldSymbol)
        {
            var ghostFixedList = Roslyn.Extensions.GetAttribute(fieldSymbol, "Unity.NetCode", "GhostFixedListCapacityAttribute");
            if (ghostFixedList == null)
                ghostFixedList = Roslyn.Extensions.GetAttribute(fieldSymbol, "", "GhostFixedListCapacity");
            if (ghostFixedList != null && ghostFixedList.NamedArguments.Length > 0)
            {
                return System.Convert.ToInt32(ghostFixedList.NamedArguments[0].Value.Value);
            }
            return 0;
        }
        private bool CanBatchPredict(ISymbol fieldSymbol)
        {
            return Roslyn.Extensions.GetAttribute(fieldSymbol, "Unity.NetCode", "BatchPredictAttribute") != null;
        }
    }
}
