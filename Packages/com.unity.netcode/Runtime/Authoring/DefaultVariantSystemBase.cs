using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Collections;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
using System.Reflection;
#endif

namespace Unity.NetCode
{
    /// <summary>
    /// <para>DefaultVariantSystemBase 是一个抽象基类，用于更新
    /// <see cref="GhostComponentSerializerCollectionData"/> 中的默认变体，该集合记录特定类型应使用的序列化变体
    /// （<see cref="GhostComponentVariationAttribute"/>）
    /// 具体实现必须实现 <see cref="RegisterDefaultVariants"/> 方法，并向字典中添加所需的类型与变体配对</para>
    /// <para>该系统必须且会同时在运行时 World 和烘焙 World 中创建，尤其在烘焙期间，
    /// `GhostAuthoringBakingSystem` 会使用 <see cref="GhostComponentSerializerCollectionSystemGroup" />，
    /// 通过默认值配置 Ghost Prefab 的元数据</para>
    /// <para>该抽象基类已经设置了正确的标志和 World 更新特性
    /// 具体实现不需要再次指定这些标志或 <see cref="WorldSystemFilterAttribute"/></para>
    /// <para><b>创建流程</b></para>
    /// <para>
    /// 所有默认变体系统都<b>必须</b>在 <see cref="GhostComponentSerializerCollectionSystemGroup"/> 之后创建，
    /// 后者负责创建默认 Ghost 变体映射 Singleton
    /// `DefaultVariantSystemBase` 已设置正确的 <see cref="CreateAfterAttribute"/>，
    /// 子类不需要再次显式添加或设置该创建顺序
    /// </para>
    /// </summary>
    /// <remarks>可以存在多个派生系统，系统会读取全部派生系统；发生冲突时会在烘焙阶段输出错误，并采用最新值</remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.BakingSystem)]
    [CreateAfter(typeof(GhostComponentSerializerCollectionSystemGroup))]
    [CreateBefore(typeof(DefaultVariantSystemGroup))]
    [UpdateInGroup(typeof(DefaultVariantSystemGroup))]
    public abstract partial class DefaultVariantSystemBase : SystemBase
    {
        /// <summary>
        /// 为类型定义默认变体时，必须说明该变体是否同时应用于父实体和子实体
        /// </summary>
        public readonly struct Rule
        {
            /// <summary>
            /// 所有顶层实体（即根实体或父实体）使用的变体
            /// </summary>
            /// <remarks>父实体默认会发送，即使用 <see cref="GhostFieldAttribute"/> 中定义的设置序列化所有 Ghost Field</remarks>
            public readonly System.Type VariantForParents;

            /// <summary>

            /// 所有子实体使用的变体

            /// </summary>
            /// <remarks>出于性能考虑，子实体默认使用 <see cref="DontSerializeVariant"/></remarks>
            public readonly System.Type VariantForChildren;

            /// <summary>该规则只会把变体添加到具有此组件类型的父实体
            /// 具有此组件的子实体仍使用 <see cref="DontSerializeVariant"/>，这也是子实体的默认设置
            /// <b>推荐使用这种方式</b></summary>
            /// <param name="variantForParentOnly">具有此组件类型的父实体将使用的变体</param>
            /// <returns>更新后的规则</returns>
            public static Rule OnlyParents(Type variantForParentOnly) => new Rule(variantForParentOnly, default);

            /// <summary>该规则会把同一变体添加到所有具有此组件类型的实体，即同时应用于父实体和子实体，不考虑层级关系
            /// <b>注意：序列化子实体的速度相对较慢，因此不建议这样做</b></summary>
            /// <param name="variantForBoth">所有具有此组件类型的实体将使用的变体</param>
            /// <returns>更新后的规则</returns>
            public static Rule ForAll(Type variantForBoth) => new Rule(variantForBoth, variantForBoth);

            /// <summary>该规则默认向父实体添加一种变体，并向子实体添加另一种变体
            /// <b>注意：序列化子实体的速度相对较慢，因此不建议这样做</b></summary>
            /// <param name="variantForParents">具有此组件类型的父实体将使用的变体</param>
            /// <param name="variantForChildren">具有此组件类型的子实体将使用的变体</param>
            /// <returns>更新后的规则</returns>
            public static Rule Unique(Type variantForParents, Type variantForChildren) => new Rule(variantForParents, variantForChildren);

            /// <summary>该规则只会把此变体添加到具有此组件的子实体
            /// 具有此组件的父实体将使用默认序列化器
            /// <b>注意：序列化子实体的速度相对较慢，因此不建议这样做</b></summary>
            /// <param name="variantForChildrenOnly">具有此组件类型的子实体将使用的变体</param>
            /// <returns>更新后的规则</returns>
            public static Rule OnlyChildren(Type variantForChildrenOnly) => new Rule(default, variantForChildrenOnly);

            /// <summary>

            /// 请改用静态构建方法

            /// </summary>
            /// <param name="variantForParents"><inheritdoc cref="VariantForParents"/></param>
            /// <param name="variantForChildren"><inheritdoc cref="VariantForChildren"/></param>
            private Rule(Type variantForParents, Type variantForChildren)
            {
                VariantForParents = variantForParents;
                VariantForChildren = variantForChildren;
            }

            /// <summary>
            /// Rule 的字符串表示形式，用于输出父实体和子实体的变体类型
            /// </summary>
            /// <returns></returns>
            public override string ToString() => $"Rule[parents: `{VariantForParents}`, children: `{VariantForChildren}`]";

            /// <summary>
            /// 比较两条规则，并检查它们的父实体和子实体类型是否相同
            /// </summary>
            /// <param name="other">用于相等性比较的规则</param>
            /// <returns>父实体和子实体的变体类型是否匹配</returns>
            public bool Equals(Rule other) => VariantForParents == other.VariantForParents && VariantForChildren == other.VariantForChildren;

            /// <summary>

            /// 设置 Variant 字段后生成唯一 HashCode

            /// </summary>
            /// <returns>设置 Variant 字段时返回唯一哈希码，否则返回 0</returns>
            public override int GetHashCode()
            {
                unchecked
                {
                    return ((VariantForParents != null ? VariantForParents.GetHashCode() : 0) * 397) ^ (VariantForChildren != null ? VariantForChildren.GetHashCode() : 0);
                }
            }

            internal HashRule CreateHashRule(ComponentType componentType) => new HashRule(TryGetHashElseZero(componentType, VariantForParents), TryGetHashElseZero(componentType, VariantForChildren));

            static ulong TryGetHashElseZero(ComponentType componentType, Type variantType)
            {
                if (variantType == null)
                    return 0;
                if (variantType == typeof(DontSerializeVariant))
                    return GhostVariantsUtility.DontSerializeHash;
                if (variantType == typeof(ClientOnlyVariant))
                    return GhostVariantsUtility.ClientOnlyHash;
                if (variantType == typeof(ServerOnlyVariant))
                    return GhostVariantsUtility.ServerOnlyHash;
                return GhostVariantsUtility.UncheckedVariantHash(variantType.FullName, componentType);
            }
        }

        /// <summary>

        /// <see cref="Rule"/> 的哈希版本，使其兼容 Burst

        /// </summary>
        internal readonly struct HashRule
        {
            /// <summary>
            /// <see cref="Rule.VariantForParents"/> 的哈希版本
            /// </summary>
            public readonly ulong VariantForParents;
            /// <summary>
            /// <see cref="Rule.VariantForChildren"/> 的哈希版本
            /// </summary>
            public readonly ulong VariantForChildren;

            public HashRule(ulong variantForParents, ulong variantForChildren)
            {
                VariantForParents = variantForParents;
                VariantForChildren = variantForChildren;
            }

            public override string ToString() => $"HashRule[parent: `{VariantForParents}`, children: `{VariantForChildren}`]";

            public bool Equals(HashRule other) => VariantForParents == other.VariantForParents && VariantForChildren == other.VariantForChildren;

        }

        protected sealed override void OnCreate()
        {
            // 仅使用 ComponentType -> Type 字典不足以保证正确性
            // 因此需要在这里执行一些健全性检查
            var defaultVariants = new Dictionary<ComponentType, Rule>();
            RegisterDefaultVariants(defaultVariants);

            var ghostComponentSerializerCollection = World.GetExistingSystemManaged<GhostComponentSerializerCollectionSystemGroup>();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var cache = ghostComponentSerializerCollection.ghostComponentSerializerCollectionDataCache;
            cache.ThrowIfNotInRegistrationPhase($"register `DefaultVariantSystemBase` child system `{GetType().Name}` in '{World.Name}'");
#endif
            var variantRules = ghostComponentSerializerCollection.DefaultVariantRules;
            foreach (var rule in defaultVariants)
                variantRules.SetDefaultVariant(rule.Key, rule.Value, this);
            Enabled = false;
        }

        protected sealed override void OnUpdate()
        {
        }

        /// <summary>
        /// 实现此方法，将默认的类型 -> 变体 <see cref="Rule"/> 添加到
        /// <paramref name="defaultVariants"/> 映射中
        /// </summary>
        /// <param name="defaultVariants">默认类型到变体的映射</param>
        protected abstract void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants);
    }

    /// <summary>
    /// 保存默认组件类型 -> Ghost 变体映射，参见 <see cref="GhostComponentVariationAttribute"/>
    /// 供实现抽象类 <see cref="DefaultVariantSystemBase"/> 的系统使用
    /// </summary>
    internal class GhostVariantRules
    {
        public struct RuleAssignment
        {
            public DefaultVariantSystemBase.Rule Rule;
            public SystemBase LastSystem;

            public override string ToString()
            {
                return $"{nameof(RuleAssignment)}: {Rule} assigned from LastSystem: {LastSystem.GetType()}";
            }
        }
        private NativeHashMap<ComponentType, DefaultVariantSystemBase.HashRule> DefaultVariants;

#if ENABLE_UNITY_COLLECTIONS_CHECKS || NETCODE_DEBUG
        // 用于调试，跟踪每个系统最近分配的规则
        // 当项目中存在多个负责分配默认变体的系统时，可借此定位哪个系统覆盖了默认规则
        private readonly Dictionary<ComponentType, RuleAssignment> DefaultVariantsManaged;
#endif

        public GhostVariantRules(NativeHashMap<ComponentType, DefaultVariantSystemBase.HashRule> defaultVariants)
        {
            DefaultVariants = defaultVariants;
#if ENABLE_UNITY_COLLECTIONS_CHECKS || NETCODE_DEBUG
            DefaultVariantsManaged = new Dictionary<ComponentType, RuleAssignment>(32);
#endif
        }

        /// <summary>
        /// 为指定组件类型设置默认使用的当前 <see cref="GhostComponentVariationAttribute"/> 变体
        /// <para>如果该组件的条目已经存在，新的 <paramref name="rule"/> 不会覆盖当前分配
        /// 可在注册系统上配合 CreateBefore 使用，为默认变体设置优先级</para>
        /// </summary>
        /// <param name="componentType">需要指定所用变体的组件类型</param>
        /// <param name="rule">要分配的规则</param>
        /// <param name="currentSystem">要分配该规则的系统，主要用于调试</param>
        /// <returns></returns>
        public bool TrySetDefaultVariant(ComponentType componentType, DefaultVariantSystemBase.Rule rule, SystemBase currentSystem)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ValidateVariantRule(componentType, rule, currentSystem);
#endif
            var added = DefaultVariants.TryAdd(componentType, rule.CreateHashRule(componentType));
#if ENABLE_UNITY_COLLECTIONS_CHECKS || NETCODE_DEBUG
            if (added)
                DefaultVariantsManaged[componentType] = new RuleAssignment { Rule = rule, LastSystem = currentSystem };
#endif
            return added;
        }

        /// <summary>
        /// 为指定组件类型设置默认使用的当前 <see cref="GhostComponentVariationAttribute"/> 变体
        /// 如果 <paramref name="componentType"/> 的规则已经存在，会记录错误但仍将其覆盖
        /// 如果项目需要为同一类型提供多个默认变体，请使用 TrySetDefaultVariant 和 CreateBefore
        /// </summary>
        /// <param name="componentType">需要指定所用变体的组件类型</param>
        /// <param name="rule">要分配的规则</param>
        /// <param name="currentSystem">要分配该规则的系统，主要用于调试</param>
        /// <returns></returns>
        public void SetDefaultVariant(ComponentType componentType, DefaultVariantSystemBase.Rule rule, SystemBase currentSystem)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ValidateVariantRule(componentType, rule, currentSystem);
#endif
            var newRuleHash = rule.CreateHashRule(componentType);
#if ENABLE_UNITY_COLLECTIONS_CHECKS || NETCODE_DEBUG
            if (DefaultVariantsManaged.TryGetValue(componentType, out var existingRule))
            {
                var rulesAreTheSame = existingRule.Rule.Equals(rule);
                if (!rulesAreTheSame)
                {
                    UnityEngine.Debug.LogError($"`Overriding the default variant rule for type `{componentType.ToFixedString()}` with '{rule}' ('{newRuleHash}'). Previous rule was " +
                                          $"('{existingRule.Rule}' ('{existingRule.Rule.CreateHashRule(componentType)}'), setup by {TypeManager.GetSystemName(existingRule.LastSystem.GetType())}. " +
                                          $"In your implementation of DefaultVariantSystemBase use [CreateBefore(typeof({TypeManager.GetSystemName(existingRule.LastSystem.GetType())}))] to resolve this issue.");
                }
            }
            DefaultVariantsManaged[componentType] = new RuleAssignment{Rule = rule, LastSystem = currentSystem};
#endif
            DefaultVariants[componentType] = newRuleHash;
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        void ValidateVariantRule(ComponentType componentType, DefaultVariantSystemBase.Rule rule, ComponentSystemBase systemBase)
        {
            if (rule.VariantForParents == default && rule.VariantForChildren == default)
                throw new System.ArgumentException($"`{componentType}` has an invalid default variant rule ({rule}) defined in `{TypeManager.GetSystemName(systemBase.GetType())}` (in '{systemBase.World.Name}'), as both are `null`!");

            var managedType = componentType.GetManagedType();
            if (typeof(InputBufferData<>).IsAssignableFrom(managedType))
                throw new System.ArgumentException($"`{managedType}` is of type `IInputBufferData`, which must get its default variants from the `IInputComponentData` that it is code-generated from. Replace this dictionary entry ({rule}) with the `IInputComponentData` type in system `{TypeManager.GetSystemName(systemBase.GetType())}`, in '{systemBase.World.Name}'!");

            ValidateUserDefinedDefaultVariantRule(componentType, rule.VariantForParents, systemBase);
            ValidateUserDefinedDefaultVariantRule(componentType, rule.VariantForChildren, systemBase);
        }

        void ValidateUserDefinedDefaultVariantRule(ComponentType componentType, Type variantType, ComponentSystemBase systemBase)
        {
            // 如果变体是默认序列化器，则无需验证
            if (variantType == default || variantType == componentType.GetManagedType())
                return;

            var isInput = typeof(ICommandData).IsAssignableFrom(componentType.GetManagedType());
            if (variantType == typeof(ClientOnlyVariant) || variantType == typeof(ServerOnlyVariant) || variantType == typeof(DontSerializeVariant))
            {
                if (isInput)
                    throw new System.ArgumentException($"System `{GetType().FullName}` is attempting to set a default variant for an `ICommandData` type: `{componentType}`, but the type of the variant is `{variantType.FullName}`! Ensure you use a serialized variant with `GhostPrefabType.All`!");
                return;
            }

            var variantAttr = variantType.GetCustomAttribute<GhostComponentVariationAttribute>();
            if (variantAttr == null)
                throw new System.ArgumentException($"Invalid type registered as default variant. GhostComponentVariationAttribute not found for type `{variantType.FullName}`, cannot use it as the default variant for `{componentType}`! Defined in system `{TypeManager.GetSystemName(systemBase.GetType())}`!");

            var managedType = componentType.GetManagedType();
            if (variantAttr.ComponentType != managedType)
                throw new System.ArgumentException($"`{variantType.FullName}` is not a variation of component `{componentType}`, cannot use it as a default variant in system `{TypeManager.GetSystemName(systemBase.GetType())}`!");
        }
#endif
    }
}
