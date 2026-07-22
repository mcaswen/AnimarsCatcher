using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{

    // TODO：如果可行则改为 internal
    /// <summary>
    /// <para>
    /// 仅供内部使用，存储所有 NetCode 相关组件各自的序列化策略及元数据
    /// 以及这些组件的所有 Variant，参见 <see cref="GhostComponentVariationAttribute"/>
    /// 因此，它会映射到代码生成的 <see cref="GhostComponentSerializer"/>，即默认 Serializer
    /// 以及所有用户创建的 Variant，参见 <see cref="GhostComponentVariationAttribute"/>
    /// 此类型还存储 <see cref="DontSerializeVariant"/>、<see cref="ClientOnlyVariant"/> 和 <see cref="ServerOnlyVariant"/> 的实例
    /// </para>
    /// <para>
    /// 注意：Serializer 被视为可选项，某个类型的序列化策略完全可以是什么也不做
    /// 例如，某个组件通过 <see cref="GhostComponentVariationAttribute"/> 声明了 Variant
    /// 但没有为其生成序列化代码，即基础组件声明中指定了 <see cref="GhostInstance"/> 特性
    /// 而 Variant 中未指定，这类 Variant 称为空 Variant
    /// </para>
    /// </summary>
    /// <remarks>此类型在 1.0 版本中由 VariantType 重命名而来</remarks>
    public struct ComponentTypeSerializationStrategy : IComparable<ComponentTypeSerializationStrategy>
    {
        /// <summary>
        /// 表示此策略为何是或不是默认策略，值越高优先级越高
        /// </summary>
        /// <remarks>这是 Flags 枚举，因此一个策略可能因多个原因被视为默认策略</remarks>
        [Flags]
        public enum DefaultType : byte
        {
            /// <summary>
            /// 不是默认策略
            /// </summary>
            NotDefault = 0,
            /// <summary>
            /// Editor 测试 Variant，仅在确实没有其他默认项时将其作为默认项
            /// </summary>
            YesAsEditorDefault = 1 << 1,
            /// <summary>
            /// 仅因找不到合适选项而作为默认 Variant
            /// </summary>
            YesAsIsFallback = 1 << 2,
            /// <summary>
            /// 子实体默认使用 <see cref="DontSerializeVariant"/>
            /// </summary>
            YesAsIsChildDefaultingToDontSerializeVariant = 1 << 3,
            /// <summary>
            /// 应使用默认 Serializer，仅在需要序列化时适用
            /// 对子实体而言，仅当用户在默认 Serializer 上设置 <see cref="GhostComponentAttribute.SendDataForChildEntity"/> 标志时适用
            /// </summary>
            YesAsIsDefaultSerializerAndDefaultIsUnchanged = 1 << 4,
            /// <summary>
            /// 如果开发者只为某类型创建了一个 Variant，则该 Variant 成为默认项
            /// </summary>
            YesAsOnlyOneVariantBecomesDefault = 1 << 5,
            /// <summary>
            /// 这是用户通过 <see cref="DefaultVariantSystemBase"/> 选择的默认 Variant，因此优先级高于 <see cref="YesAsIsDefaultSerializerAndDefaultIsUnchanged"/>
            /// </summary>
            YesAsIsUserSpecifiedNewDefault = 1 << 6,
            /// <summary>
            /// 用户通过 ComponentOverride 将其标记为默认 Variant，拥有最高优先级
            /// </summary>
            YesViaUserSpecifiedNamedDefaultOrHash = 1 << 7,
        }

        /// <summary>
        /// 指向 <see cref="GhostComponentSerializerCollectionData.SerializationStrategies"/> 列表的索引
        /// </summary>
        public short SelfIndex;
        /// <summary>
        /// 指向 <see cref="GhostComponentSerializerCollectionData.Serializers"/> 的索引
        /// </summary>
        /// <remarks>Serializer 是可选的，如果此类型不序列化组件数据则为 0</remarks>
        public short SerializerIndex;
        /// <summary>
        /// 此 Variant 关联的组件
        /// </summary>
        public ComponentType Component;
        /// <summary>
        /// 策略的 Hash 标识符，传入 <see cref="GhostComponentSerializerCollectionData.SelectSerializationStrategyForComponentWithHash"/> 使用时应为非零值
        /// </summary>
        public ulong Hash;
        /// <summary>
        /// Variant 声明中的 <see cref="GhostInstance"/> 所设置的 <see cref="GhostPrefabType"/> 值
        /// 部分 Variant 会修改序列化规则，默认值为 <see cref="GhostPrefabType.All"/>
        /// </summary>
        public GhostPrefabType PrefabType;
        ///<summary>
        /// 如果能够确定，则覆盖数据发送到的客户端类型
        /// </summary>
        public GhostSendType SendTypeOptimization;
        /// <summary>
        /// 参见 <see cref="DefaultType"/>
        /// </summary>
        public DefaultType DefaultRule;
        // TODO：为以下字段创建一个 byte 类型的 Flags 枚举
        /// <summary>
        /// 如果这是该组件类型的默认 Serializer，则为 true
        /// 即由组件定义本身生成的 Serializer，参见 <see cref="GhostFieldAttribute"/> 和 <see cref="GhostComponentAttribute"/>
        /// </summary>
        /// <remarks>Translation 等类型本身未定义任何 GhostField，因此没有默认 Serializer，但它们拥有可序列化的 Variant</remarks>
        public byte IsDefaultSerializer;
        /// <remarks>如果这是 Editor 测试 Variant，则为 true，强制将其视为默认项以便编写测试</remarks>
        /// <inheritdoc cref="GhostComponentVariationAttribute.IsTestVariant"/>
        public byte IsTestVariant;
        /// <summary>
        /// 如果此 Variant 或其对应类型上的 <see cref="GhostComponentAttribute.SendDataForChildEntity"/> 标志为 true，则为 true
        /// </summary>
        public byte SendForChildEntities;
        /// <summary>
        /// 如果代码生成器判定这是输入组件或其 Variant，则为 true
        /// </summary>
        public byte IsInputComponent;
        /// <summary>
        /// 如果代码生成器判定这是输入 Buffer，则为 true
        /// </summary>
        public byte IsInputBuffer;
        /// <summary>
        /// 此组件是否明确禁止覆盖，与 Variant 数量无关
        /// </summary>
        public byte HasDontSupportPrefabOverridesAttribute;

        /// <summary>
        /// 参见 <see cref="IsInputComponent"/> 和 <see cref="IsInputBuffer"/>
        /// </summary>
        internal byte IsInput => (byte) (IsInputComponent | IsInputBuffer);
        /// <summary>
        /// 类型名称，如果存在 Variant 且其显示名称非空，则使用 Variant 的显示名称
        /// </summary>
        public FixedString64Bytes DisplayName;
        /// <summary>
        /// 如果此 Variant 会序列化其数据，则为 true
        /// </summary>
        /// <remarks>如果类型具有 <see cref="GhostEnabledBitAttribute"/> 特性，此值同样为 true</remarks>
        public byte IsSerialized => (byte) (SerializerIndex >= 0 ? 1 : 0);
        /// <summary>
        /// 如果此 Variant 是 <see cref="DontSerializeVariant"/>，则为 true
        /// </summary>
        public bool IsDontSerializeVariant => Hash == GhostVariantsUtility.DontSerializeHash;
        /// <summary>
        /// 如果此 Variant 是 <see cref="ClientOnlyVariant"/>，则为 true
        /// </summary>
        public bool IsClientOnlyVariant => Hash == GhostVariantsUtility.ClientOnlyHash;

        /// <summary>
        /// 检查两个 VariantType 是否相同
        /// </summary>
        /// <param name="other">Variant 类型</param>
        /// <returns><paramref name="other"/> 是否相同</returns>
        public int CompareTo(ComponentTypeSerializationStrategy other)
        {
            if (IsSerialized != other.IsSerialized)
                return IsSerialized - other.IsSerialized;
            if (DefaultRule != other.DefaultRule)
                return (int)DefaultRule - (int)other.DefaultRule;
            if (Hash != other.Hash)
                return Hash < other.Hash ? -1 : 1;
            return 0;
        }

        /// <summary>
        /// 将实例转换为字符串表示形式
        /// </summary>
        /// <returns>实例的字符串表示形式</returns>
        public override string ToString() => ToFixedString().ToString();

        /// <summary>
        /// 在 Burst 中返回兼容 Burst 的调试字符串，否则返回包含更多信息的字符串
        /// </summary>
        /// <returns>调试字符串</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString512Bytes ToFixedString()
        {
            var fs = new FixedString512Bytes((FixedString32Bytes) $"SS<");
            fs.Append(Component.GetDebugTypeName());
            fs.Append((FixedString128Bytes) $">[{DisplayName}, H:{Hash}, DR:{(int) DefaultRule}, SI:{SerializerIndex}, PT:{(int) PrefabType}, self:{SelfIndex}, child:{SendForChildEntities}]");
            return fs;
        }

        internal static FixedString32Bytes GetDefaultDisplayName(ComponentTypeSerializationStrategy.DefaultType defaultRule)
        {
            if ((defaultRule & ComponentTypeSerializationStrategy.DefaultType.YesViaUserSpecifiedNamedDefaultOrHash) != 0)
                return "Chosen";
            if ((defaultRule & ComponentTypeSerializationStrategy.DefaultType.YesAsIsUserSpecifiedNewDefault) != 0)
                return "User-Specified Default";
            if ((defaultRule & ComponentTypeSerializationStrategy.DefaultType.YesAsOnlyOneVariantBecomesDefault) != 0)
                return "Default as Only Variant";
            if ((defaultRule & ComponentTypeSerializationStrategy.DefaultType.YesAsIsDefaultSerializerAndDefaultIsUnchanged) != 0)
                return "Default Serializer";
            if ((defaultRule & ComponentTypeSerializationStrategy.DefaultType.YesAsIsFallback) != 0)
                return "Fallback";
            if ((defaultRule & ComponentTypeSerializationStrategy.DefaultType.YesAsEditorDefault) != 0)
                return "Editor-Only Default";
            return defaultRule == DefaultType.NotDefault ? "" : "Default";
        }
    }

    /// <summary>
    /// 所有代码生成系统的父组，这些系统在运行时将 Ghost 组件 Serializer 注册到 <see cref="GhostCollection"/>
    /// 更具体地说，是注册到 <see cref="GhostComponentSerializer.State"/> 集合
    /// 仅供内部使用，不要向此组添加系统
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.BakingSystem,
        WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.BakingSystem)]
    [CreateBefore(typeof(DefaultVariantSystemGroup))]
    public partial class GhostComponentSerializerCollectionSystemGroup : ComponentSystemGroup
    {
        /// <summary>
        /// HashSet 和 HashTable 具有固定容量
        /// </summary>
        /// <remarks>如果 Variant 很多则增加此值，硬编码的倍数源于 DontSerializeVariant</remarks>
        public static int CollectionDefaultCapacity = (int) (DynamicTypeList.MaxCapacity * 2.2);

        /// <summary>
        /// 用于规避 GetSingleton 在第 0 帧无法工作的临时方案，尽管创建顺序是正确的，具体原因尚不明确
        /// </summary>
        internal GhostComponentSerializerCollectionData ghostComponentSerializerCollectionDataCache { get; private set; }

        /// <summary>
        /// 在 World 创建期间存储默认 Ghost 组件 Variant 映射
        /// </summary>
        internal GhostVariantRules DefaultVariantRules { get; private set; }

        struct NeverCreatedSingleton : IComponentData
        {}

        protected override void OnCreate()
        {
            base.OnCreate();
            RequireForUpdate<NeverCreatedSingleton>();
            var worldNameShortened = new FixedString32Bytes();
            FixedStringMethods.CopyFromTruncated(ref worldNameShortened, World.Unmanaged.Name);
            ghostComponentSerializerCollectionDataCache = new GhostComponentSerializerCollectionData
            {
                WorldName = worldNameShortened,
                CollectionFinalized = new NativeReference<byte>(Allocator.Persistent),
                Serializers = new NativeList<GhostComponentSerializer.State>(CollectionDefaultCapacity, Allocator.Persistent),
                SerializationStrategies = new NativeList<ComponentTypeSerializationStrategy>(CollectionDefaultCapacity, Allocator.Persistent),
                SerializationStrategiesComponentTypeMap = new NativeParallelMultiHashMap<ComponentType, short>(CollectionDefaultCapacity, Allocator.Persistent),
                DefaultVariants = new NativeHashMap<ComponentType, DefaultVariantSystemBase.HashRule>(CollectionDefaultCapacity, Allocator.Persistent),
                InputComponentBufferMap = new NativeHashMap<ComponentType, ComponentType>(CollectionDefaultCapacity, Allocator.Persistent),
            };
            DefaultVariantRules = new GhostVariantRules(ghostComponentSerializerCollectionDataCache.DefaultVariants);
            // 注意：此实体会在 BakingWorld 中被销毁，因为首次导入打开场景时会清理 World 中的所有实体
            // 因此，如果当前是 Baking World 且此实体缺失，GhostAuthoringBakingSystem 会延迟重建它
            EntityManager.CreateSingleton(ghostComponentSerializerCollectionDataCache);
        }

        protected override void OnDestroy()
        {
            ghostComponentSerializerCollectionDataCache.Dispose();
            ghostComponentSerializerCollectionDataCache = default;
            DefaultVariantRules = null;
            base.OnDestroy();
        }
    }

    /// <summary>
    /// 可直接复制的 <see cref="GhostComponentSerializerCollectionSystemGroup"/> 数据，仅供内部使用
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    [BurstCompile]
    public struct GhostComponentSerializerCollectionData : IComponentData
    {
        /// <summary>
        /// 注册阶段为 0
        /// <br/>Serializer 完成最终化后为 1
        /// <br/>GhostCollectionSystem 完成 Ghost 集合最终化后为 2
        /// </summary>
        internal NativeReference<byte> CollectionFinalized;

        /// <summary>
        /// 所有 Serializer，用于将 <see cref="ComponentType"/> 序列化到 Snapshot
        /// </summary>
        internal NativeList<GhostComponentSerializer.State> Serializers;
        /// <summary>
        /// 存储所有已知的代码强制默认 Variant
        /// </summary>
        internal NativeHashMap<ComponentType, DefaultVariantSystemBase.HashRule> DefaultVariants;
        /// <summary>
        /// 每个 NetCode 相关 ComponentType 都需要序列化策略，此字段存储全部策略
        /// </summary>
        internal NativeList<ComponentTypeSerializationStrategy> SerializationStrategies;
        /// <summary>
        /// 将给定 <see cref="ComponentType"/> 映射到 <see cref="SerializationStrategies"/> 集合中的条目
        /// </summary>
        internal NativeParallelMultiHashMap<ComponentType, short> SerializationStrategiesComponentTypeMap;
        /// <summary>
        /// 用于查找 IInputComponentData 类型对应 Buffer 类型的映射，仅供烘焙使用
        /// </summary>
        internal NativeHashMap<ComponentType, ComponentType> InputComponentBufferMap;
        /// <summary>
        /// 用于调试和异常字符串
        /// </summary>
        internal FixedString32Bytes WorldName;

        static readonly FixedString512Bytes registerASerializationStrategy = "register a SerializationStrategy";

        ulong HashGhostComponentSerializer(in GhostComponentSerializer.State comp)
        {
            // 以组件类型的稳定 Hash 作为良好起点
            var compHash = TypeManager.GetTypeInfo(comp.ComponentType.TypeIndex).StableTypeHash;
            if(compHash == 0)
                throw new InvalidOperationException($"'{WorldName}': Unexpected 0 hash for type {comp.ComponentType}!");
            compHash = TypeHash.CombineFNV1A64(compHash, comp.GhostFieldsHash);
            // ComponentSize 可能受 #ifdef 或其他编译及平台规则影响，因此不能参与计算
            // 保留下面的注释代码以明确说明为何不考虑此字段
            //compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.ComponentSize));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.SnapshotSize));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.ChangeMaskBits));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64((int)comp.SendToOwner));
            return compHash;
        }

        /// <summary>
        /// 由代码生成系统用于注册 SerializationStrategy
        /// 仅供内部使用
        /// </summary>
        /// <param name="serializationStrategy">要注册的策略</param>
        public void AddSerializationStrategy(ref ComponentTypeSerializationStrategy serializationStrategy)
        {
            ThrowIfNotInRegistrationPhase(registerASerializationStrategy);

            // 验证 Source Generator 生成的 Hash 不发生冲突
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ThrowIfNoHash(serializationStrategy.Hash, serializationStrategy.ToFixedString());
            if (serializationStrategy.DisplayName.IsEmpty)
            {
                UnityEngine.Debug.LogError($"{serializationStrategy.ToFixedString()} doesn't have a valid DisplayName! Ensure you set it, even if it's just to the ComponentType name.");
                serializationStrategy.DisplayName.CopyFromTruncated(serializationStrategy.Component.ToFixedString());
            }

            foreach (var existingSSIndex in SerializationStrategiesComponentTypeMap.GetValuesForKey(serializationStrategy.Component))
            {
                var existingSs = SerializationStrategies[existingSSIndex];
                if (existingSs.Hash == serializationStrategy.Hash || existingSs.DisplayName == serializationStrategy.DisplayName)
                {
                    UnityEngine.Debug.LogError($"{serializationStrategy.ToFixedString()} has the same Hash or DisplayName as already-added one (below)! Likely error in code-generation, must fix!\n{existingSs.ToFixedString()}!");
                }
            }
#endif

            AddSerializationStrategyInternal(ref serializationStrategy);
        }

        /// <summary>
        /// 注册 SerializationStrategy 的内部方法
        /// 只要属于 <see cref="DontSerializeVariant"/>、<see cref="ClientOnlyVariant"/> 或 <see cref="ServerOnlyVariant"/> 等特殊类型，就允许 Hash 冲突
        /// </summary>
        /// <remarks>
        /// 注意，根据上下文可能会生成大量 <see cref="DontSerializeVariant"/>，每种类型最多两个
        /// </remarks>
        private void AddSerializationStrategyInternal(ref ComponentTypeSerializationStrategy serializationStrategy)
        {
            serializationStrategy.SelfIndex = (short) SerializationStrategies.Length;
            SerializationStrategies.Add(serializationStrategy);
            SerializationStrategiesComponentTypeMap.Add(serializationStrategy.Component, serializationStrategy.SelfIndex);
        }

        /// <summary>
        /// 由代码生成系统使用，仅供内部使用
        /// 将生成的 Ghost Serializer 添加到 <see cref="GhostComponentSerializer.State"/> 集合
        /// </summary>
        /// <param name="state">Serializer 状态</param>
        public void AddSerializer(GhostComponentSerializer.State state)
        {
            ThrowIfNotInRegistrationPhase("register a Serializer");

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ThrowIfNoHash(state.VariantHash, $"'{WorldName}': AddSerializer for '{state.ComponentType}'.");
#endif

            // 将 Serializer 映射到 SerializationStrategy
            MapSerializerToStrategy(ref state, (short) Serializers.Length);
            state.SerializerHash = HashGhostComponentSerializer(state);
            Serializers.Add(state);
        }

        /// <summary>
        /// 系统无法预知还有多少代码生成类型尚未注册，因此使用一个标志表示所有类型均已创建
        /// 如果用户在所有查询创建完成前访问此集合，过去会静默地将 GhostField 默认设为 DontSerializeVariant
        /// 此检查通过抛出异常明确指出这类用户错误
        /// </summary>
        /// <param name="context">本次调用的上下文，用于辅助错误报告</param>
        /// <exception cref="InvalidOperationException">用户代码查询过早时抛出</exception>
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void ThrowIfNotInRegistrationPhase(in FixedString512Bytes context)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if(!CollectionFinalized.IsCreated)
                throw new InvalidOperationException($"'{WorldName}': Fatal error: Attempting to {context} but OnCreate has not yet been called! You must delay this registration call to after the creation of the `GhostComponentSerializerCollectionSystemGroup`.");
            if (CollectionFinalized.Value != 0)
                throw new InvalidOperationException($"'{WorldName}': Fatal error: Attempting to {context} but we've already finalized or queried this collection! You must ensure that, when called from `OnCreate`, your system uses attribute `[CreateBefore(typeof(DefaultVariantSystemGroup))]`.");
#endif
        }

        /// <summary>
        /// 系统无法预知还有多少代码生成类型尚未注册，因此使用一个标志表示所有类型均已创建
        /// 如果用户在所有查询创建完成前访问此集合，会静默地将 GhostField 默认设为 DontSerializeVariant
        /// </summary>
        /// <param name="context">本次调用的上下文，用于辅助错误报告</param>
        /// <exception cref="InvalidOperationException">用户代码查询过早时抛出</exception>
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void ThrowIfCollectionNotFinalized(in FixedString512Bytes context)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!CollectionFinalized.IsCreated || CollectionFinalized.Value == 0)
                throw new InvalidOperationException($"'{WorldName}': Fatal error: Attempting to {context} but we have not yet finalized this collection! You must delay your call until after the creation of the `DefaultVariantSystemGroup` (e.g. `[CreateAfter(typeof(DefaultVariantSystemGroup))]` on your system).");
#endif
       }

        /// <summary>
        /// 查找给定 IInputComponentData 对应的 Buffer 组件类型
        /// </summary>
        /// <param name="inputType">组件类型</param>
        /// <param name="bufferType">Buffer 类型</param>
        /// <returns>组件存在可用的关联 Buffer 时为 true，否则为 false</returns>
        [Obsolete("TryGetBufferForInputComponent has been deprecated. In order to find the buffer associated with an IInputComponentData please just use" +
                  "IInputBuffer<T> where T is the IInputComponentData type you are looking for.", false)]
        public bool TryGetBufferForInputComponent(ComponentType inputType, out ComponentType bufferType)
        {
            bufferType = default;
            return false;
        }

        /// <summary>
        /// 由代码生成系统使用，仅供内部使用
        /// 添加从 IInputComponentData 到其应使用 Buffer 的映射
        /// </summary>
        /// <param name="inputType">输入类型</param>
        /// <param name="bufferType">Buffer 类型</param>
        public void AddInputComponent(ComponentType inputType, ComponentType bufferType)
        {
            InputComponentBufferMap.TryAdd(inputType, bufferType);
        }
        internal void MapSerializerToStrategy(ref GhostComponentSerializer.State state, short serializerIndex)
        {
            foreach (var ssIndex in SerializationStrategiesComponentTypeMap.GetValuesForKey(state.ComponentType))
            {
                ref var ss = ref SerializationStrategies.ElementAt(ssIndex);
                if (ss.Hash == state.VariantHash)
                {
                    state.SerializationStrategyIndex = ssIndex;
                    ss.SerializerIndex = serializerIndex;
                    return;
                }
            }

            throw new InvalidOperationException($"{WorldName}: No SerializationStrategy found for Serializer with Hash: {state.VariantHash}!");
        }

        /// <summary>
        /// 从 <see cref="GetAllAvailableSerializationStrategiesForType"/> 返回的可用 Variant 中
        /// 为此 <see cref="componentType"/> 查找 <see cref="chosenVariant"/>
        /// </summary>
        /// <param name="componentType">要查找序列化策略的类型</param>
        /// <param name="chosenVariantHash">设置后表示应使用特定 Variant，0 表示使用默认项
        /// 运行时已将子实体 Variant 转换为特定 Serializer 或 DontSerializeVariant
        /// 缺少这一转换时，此逻辑将无法正常工作</param>
        /// <param name="isRoot">实体为根实体时为 true，为子实体时为 false
        /// 需要区分两者是因为子实体默认使用 <see cref="DontSerializeVariant"/></param>
        [BurstCompile]
        internal ComponentTypeSerializationStrategy GetCurrentSerializationStrategyForComponent(ComponentType componentType, ulong chosenVariantHash, bool isRoot)
        {
            using var available = GetAllAvailableSerializationStrategiesForType(componentType, chosenVariantHash, isRoot);
            return SelectSerializationStrategyForComponentWithHash(componentType, chosenVariantHash, in available, isRoot);
        }

        /// <inheritdoc cref="GetCurrentSerializationStrategyForComponent"/>
        internal ComponentTypeSerializationStrategy SelectSerializationStrategyForComponentWithHash(ComponentType componentType, ulong chosenVariantHash, in NativeList<ComponentTypeSerializationStrategy> available, bool isRoot)
        {
            if (available.Length != 0)
            {
                if (chosenVariantHash == 0)
                {
                    // 查找最佳默认序列化策略
                    var bestIndex = 0;
                    for (var i = 1; i < available.Length; i++)
                    {
                        var bestSs = available[bestIndex];
                        var availableSs = available[i];
                        if (availableSs.DefaultRule > bestSs.DefaultRule)
                        {
                            bestIndex = i;
                        }
                        else if (availableSs.DefaultRule == bestSs.DefaultRule)
                        {
                            if (availableSs.DefaultRule != ComponentTypeSerializationStrategy.DefaultType.NotDefault)
                            {
                                BurstCompatibleErrorWithAggregate(componentType, in available, $"Type `{componentType.ToFixedString()}` (isRoot: {isRoot} with chosenVariantHash '{chosenVariantHash}') has 2 or more default serialization strategies with the same `DefaultRule` ({(int) availableSs.DefaultRule})! Using the first.");
                            }
                        }
                    }

                    var finalVariant = available[bestIndex];
                    if (finalVariant.DefaultRule != ComponentTypeSerializationStrategy.DefaultType.NotDefault)
                    {
                        // 找到的最佳默认 Variant 本身不在子实体上序列化，因此替换为 DontSerializeVariant
                        if (!finalVariant.IsDontSerializeVariant && !isRoot && finalVariant.SendForChildEntities == 0)
                        {
                            if (TryFindDontSerializeIndex(in available, out int dontSerializeIndex))
                                return available[dontSerializeIndex];
                            return ConstructDontSerializeVariant(in available, componentType, ComponentTypeSerializationStrategy.DefaultType.YesAsIsFallback, bestIndex, nameof(DontSerializeVariant));
                        }
                        return finalVariant;
                    }

                    // 查找失败，改用最稳妥的回退项
                    var fallback = GetSafestFallbackVariantUponError(available);
                    BurstCompatibleErrorWithAggregate(componentType, in available, $"Type `{componentType.ToFixedString()}` (isRoot: {isRoot} with chosenVariantHash '{chosenVariantHash}') has NO default serialization strategies! Calculating the safest fallback guess ('{fallback.ToFixedString()}').");
                    return fallback;
                }

                // 按 Hash 查找完全匹配的 Variant
                foreach (var variant in available)
                    if (variant.Hash == chosenVariantHash)
                        return variant;

                // 未找到匹配项，尝试获取最稳妥的回退项
                if (available.Length != 0)
                {
                    var fallback = GetSafestFallbackVariantUponError(available);
                    BurstCompatibleErrorWithAggregate(componentType, in available, $"Failed to find serialization strategy for `{componentType.ToFixedString()}` (isRoot: {isRoot}) with chosenVariantHash '{chosenVariantHash}'! There are {available.Length} serialization strategies available, so calculating the safest fallback guess ('{fallback.ToFixedString()}').");
                    return fallback;
                }
            }

            // 没有找到任何可用项，执行回退
            BurstCompatibleErrorWithAggregate(componentType, in available, $"Unable to find chosenVariantHash '{chosenVariantHash}' for `{componentType.ToFixedString()}` (isRoot: {isRoot}) as no serialization strategies available for type! Fallback is `DontSerializeVariant`.");
            TryFindDefaultSerializerIndex(in available, out var sourceVariantIndex);
            if (TryFindDontSerializeIndex(in available, out var dontSerializeIndexFallback))
                return available[dontSerializeIndexFallback];
            return ConstructDontSerializeVariant(in available, componentType, ComponentTypeSerializationStrategy.DefaultType.YesAsIsFallback, sourceVariantIndex, $"{nameof(DontSerializeVariant)} (Fallback)");
        }

        /// <summary>
        /// 无法找到请求的 Variant 时，通过此方法查找最佳回退项
        /// </summary>
        static ComponentTypeSerializationStrategy GetSafestFallbackVariantUponError(in NativeList<ComponentTypeSerializationStrategy> available)
        {
            // 优先序列化 Ghost 的全部数据，虽然可能浪费带宽，但数据能够得到复制，因此最为稳妥
            for (var i = 0; i < available.Length; i++)
            {
                if (available[i].IsSerialized != 0 && available[i].IsDefaultSerializer != 0)
                    return available[i];
            }

            // 否则回退到任意可序列化 Variant
            for (var i = 0; i < available.Length; i++)
            {
                if (available[i].IsSerialized != 0)
                    return available[i];
            }

            // 仍未找到时回退到列表最后一项，该项最可能是自定义 Variant
            return available[available.Length - 1];
        }

        /// <summary>
        /// <para><b>一次应用全部 Variant 规则，查找给定类型的所有可用 Variant</b></para>
        /// <para>由于任意组件都可能存在多个 Variant，因此需要处理若干重要用例</para>
        /// <para>对于 <see cref="InputBufferData{T}"/>，此方法返回其 <see cref="IInputComponentData"/> Authoring 结构可用的 Variant</para>
        /// <para>注意，返回的默认 Variant 数量不一定为 1，可能更多或更少</para>
        /// </summary>
        /// <param name="componentType">要查找 Variant 的类型</param>
        /// <param name="chosenVariantHash">设置后表示明确请求某个 Variant 作为覆盖项，零表示查找默认项</param>
        /// <param name="isRoot">此组件位于根实体上时为 true</param>
        /// <returns>此 componentType 的所有可用 Variant 列表</returns>
        [BurstCompile]
        public NativeList<ComponentTypeSerializationStrategy> GetAllAvailableSerializationStrategiesForType(ComponentType componentType, ulong chosenVariantHash, bool isRoot)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ThrowIfCollectionNotFinalized($"attempting to GetAllAvailableSerializationStrategiesForType({componentType.ToFixedString()}, hash: {chosenVariantHash}, isRoot: {isRoot})");
#endif

            var availableVariants = new NativeList<ComponentTypeSerializationStrategy>(4, Allocator.Temp);
            var numCustomVariants = 0;
            var customVariantIndex = -1;
            var alreadyAddedDontSerializeVariant = false;
            var alreadyAddedClientOnlyVariant = false;
            var alreadyAddedServerOnlyVariant = false;

            // 代码生成的 SerializationStrategy 在此建立映射
            // 本方法创建的任何序列化策略也会加入此映射，因此它实际上充当动态缓存
            foreach (var strategyLookup in SerializationStrategiesComponentTypeMap.GetValuesForKey(componentType))
            {
                var strategy = SerializationStrategies[strategyLookup];
                strategy.DefaultRule = CalculateDefaultTypeForSerializer(componentType, isRoot, strategy.IsSerialized > 0, strategy.IsDefaultSerializer, strategy.IsInput, strategy.Hash, ref strategy.SendForChildEntities, chosenVariantHash);
                AddAndCount(ref strategy);
            }

            // ClientOnlyVariant 的特殊处理
            ComponentTypeSerializationStrategy.DefaultType defaultType;
            if (!alreadyAddedClientOnlyVariant && VariantIsUserSpecifiedDefaultRule(componentType, GhostVariantsUtility.ClientOnlyHash, isRoot, chosenVariantHash, out defaultType))
            {
                var clientOnlyVariant = new ComponentTypeSerializationStrategy
                {
                    Component = componentType,
                    DefaultRule = defaultType,
                    SerializerIndex = -1, // 仅存在于客户端，因此不序列化
                    SelfIndex = -1, // 使用硬编码索引查找
                    PrefabType = GhostPrefabType.Client,
                    Hash = GhostVariantsUtility.ClientOnlyHash,
                    DisplayName = GhostVariantsUtility.k_ClientOnlyVariant,
                };
                AddSerializationStrategyInternal(ref clientOnlyVariant);

                AddAndCount(ref clientOnlyVariant);
            }
            // ServerOnlyVariant 的特殊处理
            if (!alreadyAddedServerOnlyVariant && VariantIsUserSpecifiedDefaultRule(componentType, GhostVariantsUtility.ServerOnlyHash, isRoot, chosenVariantHash, out defaultType))
            {
                var serverOnlyVariant = new ComponentTypeSerializationStrategy
                {
                    Component = componentType,
                    DefaultRule = defaultType,
                    SerializerIndex = -1, // 仅存在于服务器，因此不序列化
                    SelfIndex = -1, // 使用硬编码索引查找
                    PrefabType = GhostPrefabType.Server,
                    Hash = GhostVariantsUtility.ServerOnlyHash,
                    DisplayName = GhostVariantsUtility.k_ServerOnlyVariant,
                };
                AddSerializationStrategyInternal(ref serverOnlyVariant);

                AddAndCount(ref serverOnlyVariant);
            }

            // DontSerializeVariant 的特殊处理
            if (!alreadyAddedDontSerializeVariant && !IsInput(availableVariants))
            {
                // 仅添加被明确请求或确有用途的 DontSerializeVariant
                if ((VariantIsUserSpecifiedDefaultRule(componentType, GhostVariantsUtility.DontSerializeHash, isRoot, chosenVariantHash, out _)) || !TryFindDontSerializeIndex(in availableVariants, out _))
                {
                    byte sendForChildEntities = 0;
                    var defaultTypeForDontSerializeVariant = CalculateDefaultTypeForSerializer(componentType, isRoot, false, 0, 0, GhostVariantsUtility.DontSerializeHash, ref sendForChildEntities, chosenVariantHash);
                    TryFindDefaultSerializerIndex(in availableVariants, out var sourceVariantIndex);
                    var dontSerializeVariant = ConstructDontSerializeVariant(availableVariants, componentType, defaultTypeForDontSerializeVariant, sourceVariantIndex, nameof(DontSerializeVariant));

                    AddAndCount(ref dontSerializeVariant);
                }
            }

            // 如果该类型只有一个自定义 Variant，则将其设为默认项
            if (numCustomVariants == 1)
            {
                ref var customVariantFallback = ref availableVariants.ElementAt(customVariantIndex);
                customVariantFallback.DefaultRule |= ComponentTypeSerializationStrategy.DefaultType.YesAsOnlyOneVariantBecomesDefault;
            }

            // 对结果进行最终排序
            availableVariants.Sort();

            return availableVariants;

            void AddAndCount(ref ComponentTypeSerializationStrategy variant)
            {
                if (IsUserCreatedVariant(variant.Hash, variant.IsDefaultSerializer))
                {
                    numCustomVariants++;
                    customVariantIndex = availableVariants.Length;
                }

                if (variant.IsTestVariant != 0)
                {
                    variant.DefaultRule |= ComponentTypeSerializationStrategy.DefaultType.YesAsEditorDefault;
                }

                // 如果用户为此特定子实体选择了该 Variant，则说明用户希望序列化它
                const ComponentTypeSerializationStrategy.DefaultType userPicked = ComponentTypeSerializationStrategy.DefaultType.YesViaUserSpecifiedNamedDefaultOrHash | ComponentTypeSerializationStrategy.DefaultType.YesAsIsUserSpecifiedNewDefault;
                var isUserSpecifiedVariant = (variant.DefaultRule & userPicked) != 0;
                if (isUserSpecifiedVariant && !isRoot) // 此处无需判断是否可序列化，后续会处理，该标志只表达用户意图
                {
                    variant.SendForChildEntities = 1;
                }

                availableVariants.Add(variant);
                alreadyAddedDontSerializeVariant |= variant.Hash == GhostVariantsUtility.DontSerializeHash;
                alreadyAddedClientOnlyVariant |= variant.Hash == GhostVariantsUtility.ClientOnlyHash;
                alreadyAddedServerOnlyVariant |= variant.Hash == GhostVariantsUtility.ServerOnlyHash;
            }

            static bool IsUserCreatedVariant(ulong variantTypeHash, byte isDefaultSerializer)
            {
                return isDefaultSerializer == 0 && variantTypeHash != GhostVariantsUtility.DontSerializeHash && variantTypeHash != GhostVariantsUtility.ClientOnlyHash;
            }
        }

        private static bool TryFindDefaultSerializerIndex(in NativeList<ComponentTypeSerializationStrategy> availableVariants, out int defaultSerializerIndex)
        {
            for (defaultSerializerIndex = 0; defaultSerializerIndex < availableVariants.Length; defaultSerializerIndex++)
            {
                if (availableVariants[defaultSerializerIndex].IsDefaultSerializer > 0)
                    return true;
            }
            defaultSerializerIndex = -1;
            return false;
        }

        private static bool TryFindDontSerializeIndex(in NativeList<ComponentTypeSerializationStrategy> availableVariants, out int dontSerializeIndex)
        {
            for (dontSerializeIndex = 0; dontSerializeIndex < availableVariants.Length; dontSerializeIndex++)
            {
                if (availableVariants[dontSerializeIndex].IsDontSerializeVariant)
                    return true;
            }
            dontSerializeIndex = -1;
            return false;
        }

        static bool IsInput(NativeList<ComponentTypeSerializationStrategy> availableVariants)
        {
            foreach (var ss in availableVariants)
                if(ss.IsInput != 0)
                    return true;
            return false;
        }

        ComponentTypeSerializationStrategy ConstructDontSerializeVariant(in NativeList<ComponentTypeSerializationStrategy> availableVariants, ComponentType componentType, ComponentTypeSerializationStrategy.DefaultType defaultRule, int sourceVariantIndex, string displayName)
        {
            var dontSerializeVariant = new ComponentTypeSerializationStrategy
            {
                Component = componentType,
                DefaultRule = default, // 加入映射后再设置此值，避免重复运行时映射失效
                SerializerIndex = -1,
                SelfIndex = -1,
                PrefabType = GhostPrefabType.All,
                Hash = GhostVariantsUtility.DontSerializeHash,
                DisplayName = displayName,
            };

            // 从默认 Serializer 复制 Variant 数据，因为此 Variant 应使用相同设置
            // 示例：用户的组件 Foo 使用 PrefabType.Server，且不在子实体上序列化
            // 因此子实体使用 DontSerializeVariant，但该 Variant 必须继承 PrefabType.Server
            if(sourceVariantIndex >= 0)
            {
                var defaultSerializer = availableVariants[sourceVariantIndex];
                dontSerializeVariant.PrefabType = defaultSerializer.PrefabType;
                dontSerializeVariant.SendTypeOptimization = defaultSerializer.SendTypeOptimization;
                dontSerializeVariant.HasDontSupportPrefabOverridesAttribute = defaultSerializer.HasDontSupportPrefabOverridesAttribute;
            }

            AddSerializationStrategyInternal(ref dontSerializeVariant);
            dontSerializeVariant.DefaultRule = defaultRule;
            return dontSerializeVariant;
        }

        internal static bool AnyVariantsAreSerialized(in NativeList<ComponentTypeSerializationStrategy> availableVariants)
        {
            foreach (var x in availableVariants)
            {
                if (x.IsSerialized != 0)
                    return true;
            }

            return false;
        }

        void BurstCompatibleErrorWithAggregate(ComponentType componentType, in NativeList<ComponentTypeSerializationStrategy> availableVariants, FixedString4096Bytes error)
        {
            error.Append(WorldName);
            error.Append(' ');
            error.Append(componentType.ToFixedString());
            if (availableVariants.IsCreated)
            {
                error.Append((FixedString64Bytes) $", {availableVariants.Length} variants available: ");
                for (var i = 0; i < availableVariants.Length; i++)
                {
                    var availableVariant = availableVariants[i];
                    error.Append('\n');
                    error.Append(i);
                    error.Append(':');
                    error.Append(availableVariant.ToFixedString());
                }
            }
            UnityEngine.Debug.LogError(error);
        }

        /// <summary>
        /// Variant 具有嵌套的默认项规则，此方法负责计算这些规则
        /// </summary>
        ComponentTypeSerializationStrategy.DefaultType CalculateDefaultTypeForSerializer(ComponentType componentType, bool isRoot, bool isSerialized, byte isDefaultSerializer, byte isInput, ulong ssHash, ref byte sendForChildEntities, ulong chosenVariantHash)
        {
            if (VariantIsUserSpecifiedDefaultRule(componentType, ssHash, isRoot, chosenVariantHash, out var defaultType))
            {
                return defaultType;
            }

            // 用户未将此项指定为默认项，因此根据规则推导默认项
            if (isSerialized)
            {
                // 子实体默认使用 DontSerializeVariant
                // 但特性可能改变此行为，使默认 Serializer 成为默认项
                if (isRoot || isInput != 0 || sendForChildEntities != 0)
                    return isDefaultSerializer != 0 ? ComponentTypeSerializationStrategy.DefaultType.YesAsIsDefaultSerializerAndDefaultIsUnchanged : ComponentTypeSerializationStrategy.DefaultType.NotDefault;
            }
            else
            {
                // 当前项是 DontSerializeVariant
                if (ssHash == GhostVariantsUtility.DontSerializeHash)
                    return ComponentTypeSerializationStrategy.DefaultType.YesAsIsChildDefaultingToDontSerializeVariant;

                // 当前项是默认但不序列化的 Variant，因此将其作为最后的回退选择
                if (isDefaultSerializer > 0)
                    return ComponentTypeSerializationStrategy.DefaultType.YesAsIsFallback;
            }
            return ComponentTypeSerializationStrategy.DefaultType.NotDefault;
        }

        bool VariantIsUserSpecifiedDefaultRule(ComponentType componentType, ulong variantTypeHash, bool isRoot, ulong chosenVariantHash, out ComponentTypeSerializationStrategy.DefaultType defaultType)
        {
            // 用户按名称请求了此 Variant
            if (variantTypeHash == chosenVariantHash)
            {
                defaultType = ComponentTypeSerializationStrategy.DefaultType.YesViaUserSpecifiedNamedDefaultOrHash;
                return true;
            }

            if (DefaultVariants.TryGetValue(componentType, out var existingRule))
            {
                var variantRule = (isRoot ? existingRule.VariantForParents : existingRule.VariantForChildren);
                if (variantRule != default)
                {
                    // 用户明确指定了默认项，因此其他默认项全部失效
                    if (variantRule == variantTypeHash)
                    {
                        defaultType = ComponentTypeSerializationStrategy.DefaultType.YesAsIsUserSpecifiedNewDefault;
                        return true;
                    }
                }
            }

            defaultType = ComponentTypeSerializationStrategy.DefaultType.NotDefault;
            return false;
        }

        /// <summary>
        /// 验证 Source Generator 为默认 Serializer 返回有效 Hash
        /// </summary>
        /// <param name="hash">要检查的 Hash</param>
        /// <param name="context">字符串上下文</param>
        /// <exception cref="InvalidOperationException">无法为 <paramref name="context"/> 添加 Variant 时抛出</exception>
        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public static void ThrowIfNoHash(ulong hash, FixedString512Bytes context)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (hash == 0)
                throw new InvalidOperationException($"Cannot add variant for context '{context}' as hash is zero! Set hashes for all variants via `GhostVariantsUtility` and ensure you've rebuilt NetCode 'Source Generators'.");
#endif
        }

        /// <summary>
        /// 释放用于存储 Ghost Serializer 策略及映射的已分配资源
        /// </summary>
        public void Dispose()
        {
            CollectionFinalized.Dispose();
            Serializers.Dispose();
            SerializationStrategies.Dispose();
            DefaultVariants.Dispose();
            SerializationStrategiesComponentTypeMap.Dispose();
            InputComponentBufferMap.Dispose();
        }

        /// <summary>
        /// 验证所有序列化策略均具有有效的 <see cref="ComponentTypeSerializationStrategy.SerializerIndex"/>
        /// 并且所有 <see cref="GhostComponentSerializer.State.SerializationStrategyIndex"/> 均已设置
        /// </summary>
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void Validate()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            for (var i = 0; i < SerializationStrategies.Length; i++)
            {
                var serializationStrategy = SerializationStrategies[i];
                UnityEngine.Assertions.Assert.AreEqual(i, serializationStrategy.SelfIndex, "SerializationStrategies[i]");
                if (serializationStrategy.SerializerIndex >= 0)
                {
                    UnityEngine.Assertions.Assert.IsTrue(serializationStrategy.SerializerIndex < Serializers.Length, "SerializationStrategies > Serializer Index in Range");
                    UnityEngine.Assertions.Assert.AreEqual(i, Serializers[serializationStrategy.SerializerIndex].SerializationStrategyIndex, "SerializationStrategies > Serializer > SerializationStrategies backwards lookup!");
                }
            }
            foreach (var serializer in Serializers)
            {
                UnityEngine.Assertions.Assert.IsTrue(serializer.SerializationStrategyIndex >= 0 && serializer.SerializationStrategyIndex < SerializationStrategies.Length, "Serializer > SerializationStrategies Index in Range");
            }
#endif
        }
    }
}
