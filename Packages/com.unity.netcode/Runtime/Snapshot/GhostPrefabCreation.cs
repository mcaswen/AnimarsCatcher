#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif

using System;
using Unity.Entities;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.NetCode.LowLevel.Unsafe;

/// <summary>
/// 指定实体应转换到哪类 World
/// 根据转换设置，部分组件可能在转换时或运行时从 Prefab 中移除
/// </summary>
public enum NetcodeConversionTarget
{
    /// <summary>
    /// 同时为客户端和服务器 World 转换
    /// </summary>
    ClientAndServer = 0,
    /// <summary>
    /// 仅为服务器 World 转换
    /// </summary>
    Server = 1,
    /// <summary>
    /// 仅为客户端 World 转换
    /// </summary>
    Client = 2
}

namespace Unity.NetCode
{
    /// <summary>
    /// 存储 Ghost 在 Authoring 时支持的模式
    /// <list>
    /// <item>Interpolated 插值模式</item>
    /// <item>Predicted 预测模式</item>
    /// <item>All 全部模式</item>
    /// </list>
    /// </summary>
    public enum GhostModeMask
    {
        /// <summary>
        /// 插值 Ghost 较为轻量，因为它们不在客户端执行模拟
        /// 其值通过 <see cref="SmoothingAction"/> 规则，根据最近处理的几个 Snapshot 进行插值
        /// 从时间线角度看，插值 Ghost 落后于服务器
        /// </summary>
        Interpolated = 1,
        /// <summary>
        /// <para>预测 Ghost 由客户端预测，在 <see cref="PredictedSimulationSystemGroup"/> 执行期间
        /// 其 <see cref="Simulate"/> 组件会启用，该组中的系统会处理这些实体
        /// 它们还会具有 <see cref="PredictedGhost"/> 组件</para>
        /// <para>预测开销较高且不具权威性，但能让预测 Ghost 更准确地参与物理交互
        /// 并使其时间线与当前客户端对齐</para>
        /// <para>预测错误由 <see cref="GhostPredictionSmoothing"/> 处理
        /// 示例参见 <see cref="DefaultTranslationSmoothingAction"/>
        /// 从时间线角度看，插值 Ghost 落后于当前客户端和服务器</para>
        /// </summary>
        /// <example>
        /// 在体育游戏中，球通常会设为预测 Ghost，使玩家能够预测与球的碰撞
        /// 使用多个球的示例参见 PredictionSwitching Sample
        /// </example>
        Predicted = 2,
        /// <summary>
        /// 同时支持两种模式，因此可以在运行时通过 <see cref="GhostPredictionSwitchingQueues"/> 切换
        /// 这称为运行时预测切换，并会禁用通过 <see cref="GhostSendType"/> 实现的模式特定优化
        /// </summary>
        All = 3,
    }

    /// <summary>
    /// Ghost 在任意给定客户端上的当前模式，用于表示复制和预测规则
    /// </summary>
    /// <inheritdoc cref="GhostModeMask"/>
    public enum GhostMode
    {
        /// <inheritdoc cref="GhostModeMask.Interpolated"/>
        Interpolated,
        /// <inheritdoc cref="GhostModeMask.Predicted"/>
        Predicted,
        /// <summary>
        /// Ghost 所有者通过 <see cref="GhostOwner"/> 指定，对该 Ghost 使用 <see cref="Predicted"/> 模式
        /// 其他所有客户端则使用 <see cref="Interpolated"/> 模式
        /// </summary>
        OwnerPredicted,
    }

    /// <summary>
    /// 指定 Ghost 复制应针对频繁的动态数据变化还是不频繁的静态数据变化进行优化
    /// </summary>
    /// <inheritdoc cref="Dynamic"/>
    /// <inheritdoc cref="Static"/>
    public enum GhostOptimizationMode
    {
        /// <summary>
        /// Dynamic 是默认优化模式
        /// 预期 Ghost 经常变化，例如每帧变化时使用
        /// 它通过分层增量压缩减小每个 Snapshot 中的 Ghost 数据大小
        /// 此模式不执行变化检查，但会积极应用增量压缩
        /// </summary>
        Dynamic,
        /// <summary>
        /// <para>Static 优化模式用于很少变化或完全不变化的 Ghost</para>
        /// <para>此模式仅在 Ghost 状态变化时向客户端复制，可显著节省带宽
        /// 但需要额外 CPU 周期执行变化检查
        /// 因此应避免对频繁改变状态的实体使用静态优化，否则额外协议位和变化检查
        /// 反而会同时增加带宽与 CPU 开销</para>
        /// </summary>
        Static,
    }

    /// <summary>
    /// 用于配置和创建 Ghost Prefab 的辅助方法与结构
    /// </summary>
    public static class GhostPrefabCreation
    {
        /// <summary>
        /// 创建 Ghost Prefab 时使用的配置
        /// </summary>
        public struct Config
        {
            /// <summary>
            /// Ghost 名称，通过代码创建 Prefab 时用于生成唯一 Ghost 类型
            /// </summary>
            public FixedString64Bytes Name;
            /// <summary>
            /// 用于唯一确定 Ghost 类型的可选 UUID5 标识符
            /// 默认情况下，生成 Prefab 的 Ghost 类型由必填 <see cref="Name"/> 与唯一 GUID 前缀组合后的 SHA1 Hash 计算
            /// 如果用户提供非默认的唯一 UUID5 GUID，则改用该值
            /// </summary>
            public Hash128 UUID5GhostType;
            /// <summary>
            /// 带宽不足以发送全部内容时，Importance 越高的 Ghost 发送越频繁
            /// 最小值为 1
            /// </summary>
            public int Importance;
            /// <summary>
            /// 表示 Ghost 的最大发送频率，类似于 <see cref="GhostSendSystemData.MinSendImportance"/>
            /// </summary>
            public byte MaxSendRate;
            /// <summary>
            /// 此 Prefab 可实例化成的 Ghost 模式，例如设为 Interpolated 后就不能将此 Prefab 用于预测
            /// </summary>
            public GhostModeMask SupportedGhostModes;
            /// <summary>
            /// 此 Ghost 的默认模式，控制客户端生成时将 Prefab 实例化为何种模式
            /// 默认值可以覆盖，模式也可在运行时更改
            /// </summary>
            public GhostMode DefaultGhostMode;
            /// <summary>
            /// Dynamic 优化模式使用多个 Baseline 以持续减小数据大小
            /// Static 优化模式在发生变化时压缩率略低，但没有变化时成本为零
            /// </summary>
            public GhostOptimizationMode OptimizationMode;
            /// <summary>
            /// 为此 Ghost 启用预序列化
            /// 预序列化可让多个连接共享部分序列化 CPU 成本，但 Ghost 未发送时也会产生该成本
            /// </summary>
            public bool UsePreSerialization;
            /// <summary>
            /// 允许预测生成 Ghost 回滚到初始生成状态并重新预测，直到收到服务器权威生成结果
            /// </summary>
            public bool PredictedSpawnedGhostRollbackToSpawnTick;
            /// <summary>
            /// 客户端 CPU 优化，在发生结构变更时强制预测 Ghost 始终尝试从上次预测继续
            /// 默认为 true，因为移除复制组件时此行为可能引入问题
            /// </summary>
            public bool RollbackPredictionOnStructuralChanges;
            /// <summary>
            /// 指示 <see cref="GhostSendSystem"/> 始终为此 Ghost Archetype 使用单个 Baseline
            /// </summary>
            public bool UseSingleBaseline;
            /// <summary>
            /// 可选的自定义确定性函数，用于获取此 Ghost 所有非烘焙且可序列化的组件类型
            /// 可序列化组件是指包含带 <see cref="GhostFieldAttribute"/> 特性的 GhostField
            /// 或带有 <see cref="GhostComponentAttribute"/> 的组件
            /// </summary>
            public PortableFunctionPointer<GhostPrefabCustomSerializer.CollectComponentDelegate> CollectComponentFunc;
        }
        /// <summary>
        /// Ghost Prefab 特定子实体上特定组件类型的标识符
        /// </summary>
        public struct Component : IEquatable<Component>
        {
            /// <summary>
            /// 组件类型
            /// </summary>
            public ComponentType ComponentType;
            /// <summary>
            /// 拥有该组件的子实体，0 表示根实体
            /// </summary>
            public int ChildIndex;
            /// <summary>
            /// 比较两个 Component，类型和实体索引均相同时视为相等
            /// </summary>
            /// <param name="other">要比较的 Component</param>
            /// <returns>类型和实体索引是否相同</returns>
            public bool Equals(Component other)
            {
                return ComponentType == other.ComponentType && ChildIndex == other.ChildIndex;
            }
            /// <summary>
            /// 根据类型和索引计算组件的唯一 Hash
            /// </summary>
            /// <returns>基于组件类型和索引的唯一 Hash</returns>
            public override int GetHashCode()
            {
                return (ComponentType.GetHashCode() * 397) ^ ChildIndex.GetHashCode();
            }
        }
        /// <summary>
        /// 修改项类型的标识符，各类型可通过按位或组合并用作 Mask
        /// </summary>
        [Flags]
        public enum ComponentOverrideType
        {
            /// <summary>
            /// 不存在覆盖项
            /// </summary>
            None = 0,
            /// <summary>
            /// 覆盖组件应存在于哪类 Prefab 上
            /// </summary>
            PrefabType = 1,
            /// <summary>
            /// 覆盖组件要复制到的客户端类型
            /// </summary>
            SendMask = 2,
            // 已弃用 SendToChild = 4
            /// <summary>
            /// 指定序列化组件时使用的 <see cref="GhostComponentVariationAttribute">Variant</see>
            /// </summary>
            Variant = 8
        }

        /// <summary>
        /// 针对特定子实体上特定组件的修改项
        /// 仅应用 OverrideType 指定的覆盖类型，其余类型会被忽略
        /// </summary>
        public struct ComponentOverride
        {
            /// <summary>
            /// 要覆盖的属性
            /// </summary>
            public ComponentOverrideType OverrideType;
            /// <summary>
            /// OverrideType 为 PrefabType 时使用的 Prefab 类型
            /// </summary>
            public GhostPrefabType PrefabType;
            /// <summary>
            /// 组件使用的新 SendMask
            /// </summary>
            public GhostSendType SendMask;
            /// <summary>
            /// 组件要使用的 Variant Hash，设为 0 表示强制使用默认项
            /// </summary>
            public ulong Variant;
        }
        struct ComponentHashComparer : System.Collections.Generic.IComparer<ComponentType>
        {
            public int Compare(ComponentType x, ComponentType y)
            {
                var hashX = TypeManager.GetTypeInfo(x.TypeIndex).StableTypeHash;
                var hashY = TypeManager.GetTypeInfo(y.TypeIndex).StableTypeHash;

                if (hashX < hashY)
                    return -1;
                if (hashX > hashY)
                    return 1;
                return 0;
            }
        }

        /// <summary>
        /// 通过强制设置版本和指定位，将普通 <see cref="Hash128"/> 转换为正确的 UUID5 Hash 格式
        /// </summary>
        /// <param name="hash128">要转换为 UUID5 格式的 Hash</param>
        /// <returns>按照 RFC 4122 设置相应字节后的新 Hash</returns>
        public static Hash128 ConvertHash128ToUUID5(Hash128 hash128)
        {
            return new Hash128(
                hash128.Value.x,
                (hash128.Value.y & (~0xf000u)) | 0x5000u, // 将版本设为 5
                (hash128.Value.z & (0x3fffffffu)) | 0x80000000u, // 将高位设为 1 和 0
                hash128.Value.w);
        }

        private static bool ValidateIsUUID5(this ref GhostType ghostType)
        {
            // 验证版本为 5 且高位为 10
            return (ghostType.guid1 & 0xf000u) == 0x5000 &&
                   (ghostType.guid2 & 0xC0000000u) == 0x80000000;
        }

        internal unsafe struct SHA1
        {
            private void UpdateABCDE(int i, ref uint a, ref uint b, ref uint c, ref uint d, ref uint e, uint f, uint k)
            {
                var tmp = ((a << 5) | (a >> 27)) + e + f + k + words[i];
                e = d;
                d = c;
                c = (b << 30) | (b >> 2);
                b = a;
                a = tmp;
            }

            private void UpdateHash()
            {
                for (int i = 16; i < 80; ++i)
                {
                    words[i] = (words[i - 3] ^ words[i - 8] ^ words[i - 14] ^ words[i - 16]);
                    words[i] = (words[i] << 1) | (words[i] >> 31);
                }

                var a = h0;
                var b = h1;
                var c = h2;
                var d = h3;
                var e = h4;

                for (int i = 0; i < 20; ++i)
                {
                    var f = (b & c) | ((~b) & d);
                    var k = 0x5a827999u;
                    UpdateABCDE(i, ref a, ref b, ref c, ref d, ref e, f, k);
                }
                for (int i = 20; i < 40; ++i)
                {
                    var f = b ^ c ^ d;
                    var k = 0x6ed9eba1u;
                    UpdateABCDE(i, ref a, ref b, ref c, ref d, ref e, f, k);
                }
                for (int i = 40; i < 60; ++i)
                {
                    var f = (b & c) | (b & d) | (c & d);
                    var k = 0x8f1bbcdcu;
                    UpdateABCDE(i, ref a, ref b, ref c, ref d, ref e, f, k);
                }
                for (int i = 60; i < 80; ++i)
                {
                    var f = b ^ c ^ d;
                    var k = 0xca62c1d6u;
                    UpdateABCDE(i, ref a, ref b, ref c, ref d, ref e, f, k);
                }
                h0 += a;
                h1 += b;
                h2 += c;
                h3 += d;
                h4 += e;
            }

            public SHA1(in FixedString128Bytes str)
            {
                h0 = 0x67452301u;
                h1 = 0xefcdab89u;
                h2 = 0x98badcfeu;
                h3 = 0x10325476u;
                h4 = 0xc3d2e1f0u;
                var bitLen = str.Length << 3;
                var numFullChunks = bitLen >> 9;
                byte* ptr = str.GetUnsafePtr();
                for (int chunk = 0; chunk < numFullChunks; ++chunk)
                {
                    for (int i = 0; i < 16; ++i)
                    {
                        words[i] = (uint)((ptr[0] << 24) | (ptr[1] << 16) | (ptr[2] << 8) | ptr[3]);
                        ptr += 4;
                    }
                    UpdateHash();
                }
                var remainingBits = (bitLen & 0x1ff);
                var remainingBytes = (remainingBits >> 3);
                var fullWords = (remainingBytes >> 2);
                for (int i = 0; i < fullWords; ++i)
                {
                    words[i] = (uint)((ptr[0] << 24) | (ptr[1] << 16) | (ptr[2] << 8) | ptr[3]);
                    ptr += 4;
                }
                var fullBytes = remainingBytes & 3;
                switch (fullBytes)
                {
                    case 3:
                        words[fullWords] = (uint)((ptr[0] << 24) | (ptr[1] << 16) | (ptr[2] << 8) | 0x80u);
                        ptr += 3;
                        break;
                    case 2:
                        words[fullWords] = (uint)((ptr[0] << 24) | (ptr[1] << 16) | (0x80u << 8));
                        ptr += 2;
                        break;
                    case 1:
                        words[fullWords] = (uint)((ptr[0] << 24) | (0x80u << 16));
                        ptr += 1;
                        break;
                    case 0:
                        words[fullWords] = (uint)((0x80u << 24));
                        break;
                }
                ++fullWords;
                if (remainingBits >= 448)
                {
                    // 需要两个数据块，一个存放剩余位，另一个存放长度
                    for (int i = fullWords; i < 16; ++i)
                        words[i] = 0;
                    UpdateHash();
                    for (int i = 0; i < 15; ++i)
                        words[i] = 0;
                    words[15] = (uint)bitLen;
                    UpdateHash();
                }
                else
                {
                    for (int i = fullWords; i < 15; ++i)
                        words[i] = 0;
                    words[15] = (uint)bitLen;
                    UpdateHash();
                }
            }

            public Hash128 ToHash128()
            {
                // 构造 GUID 并将其存入 GhostType
                return new Hash128(h0, h1, h2, h3);
            }

            private fixed uint words[80];
            private uint h0;
            private uint h1;
            private uint h2;
            private uint h3;
            private uint h4;
        }

        internal static ComponentTypeSet RemoveOnServerWorldsSharedList(Entity prefabEntityToStrip, EntityManager entityManager)
        {
            FixedList64Bytes<ComponentType> resList = new();
            // 服务器应移除 Snapshot 数据 Buffer，客户端应移除 Shared Ghost Type
            resList.Add(ComponentType.ReadWrite<SnapshotData>());
            resList.Add(ComponentType.ReadWrite<SnapshotDataBuffer>());
            if (prefabEntityToStrip != Entity.Null && entityManager.HasComponent<SnapshotDynamicDataBuffer>(prefabEntityToStrip))
                resList.Add(ComponentType.ReadWrite<SnapshotDynamicDataBuffer>());
            resList.Add(ComponentType.ReadWrite<PredictedGhostSpawnRequest>());
            ComponentTypeSet res = new ComponentTypeSet(resList);
            return res;
        }
        /// <summary>
        /// 为 Ghost Prefab 构建 Blob Asset 的辅助方法，不应直接调用
        /// </summary>
        /// <param name="ghostConfig">创建 Ghost Prefab 时使用的配置</param>
        /// <param name="entityManager">用于验证 <paramref name="rootEntity"/> 上存在的组件</param>
        /// <param name="rootEntity">此实体上的组件会用于配置结果，例如 <see cref="GhostOwner"/></param>
        /// <param name="linkedEntities"><paramref name="rootEntity"/> 的所有关联实体列表</param>
        /// <param name="allComponents">所有组件类型列表</param>
        /// <param name="componentCounts">每个索引对应的组件数量列表</param>
        /// <param name="target"><see cref="NetcodeConversionTarget"/></param>
        /// <param name="prefabTypes">要创建的不同 <see cref="GhostPrefabType"/> 类型列表</param>
        /// <param name="sendMasksOverride">SendMask 列表</param>
        /// <param name="sendToChildOverride">子实体覆盖项列表</param>
        /// <param name="variants">所有类型的 Variant Hash</param>
        /// <returns>指向 <see cref="GhostPrefabBlobMetaData"/> 的 <see cref="BlobAssetReference{T}"/></returns>
        internal static BlobAssetReference<GhostPrefabBlobMetaData> CreateBlobAsset(
            Config ghostConfig, EntityManager entityManager, Entity rootEntity, NativeArray<Entity> linkedEntities,
            NativeList<ComponentType> allComponents, NativeArray<int> componentCounts,
            NetcodeConversionTarget target, NativeArray<GhostPrefabType> prefabTypes,
            NativeArray<int> sendMasksOverride, NativeArray<ulong> variants)
        {
            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<GhostPrefabBlobMetaData>();

            // 将 Importance、支持模式、默认模式和名称存入元数据 Blob Asset
            root.Importance = ghostConfig.Importance;
            root.MaxSendRate = ghostConfig.MaxSendRate;
            root.SupportedModes = GhostPrefabBlobMetaData.GhostMode.Both;
            root.DefaultMode = GhostPrefabBlobMetaData.GhostMode.Interpolated;
            if (ghostConfig.SupportedGhostModes == GhostModeMask.Interpolated)
                root.SupportedModes = GhostPrefabBlobMetaData.GhostMode.Interpolated;
            else if (ghostConfig.SupportedGhostModes == GhostModeMask.Predicted)
            {
                root.SupportedModes = GhostPrefabBlobMetaData.GhostMode.Predicted;
                root.DefaultMode = GhostPrefabBlobMetaData.GhostMode.Predicted;
            }
            else if (ghostConfig.DefaultGhostMode == GhostMode.OwnerPredicted)
            {
                if (!entityManager.HasComponent<GhostOwner>(rootEntity))
                    throw new InvalidOperationException("OwnerPrediction mode can only be used on prefabs which have a GhostOwner");
                root.DefaultMode = GhostPrefabBlobMetaData.GhostMode.Both;
            }
            else if (ghostConfig.DefaultGhostMode == GhostMode.Predicted)
            {
                root.DefaultMode = GhostPrefabBlobMetaData.GhostMode.Predicted;
            }
            root.StaticOptimization = (ghostConfig.OptimizationMode == GhostOptimizationMode.Static);
            if (root.SupportedModes != GhostPrefabBlobMetaData.GhostMode.Interpolated)
            {
                root.PredictedSpawnedGhostRollbackToSpawnTick = ghostConfig.PredictedSpawnedGhostRollbackToSpawnTick;
                root.RollbackPredictionOnStructuralChanges = ghostConfig.RollbackPredictionOnStructuralChanges;
                root.UseSingleBaseline = ghostConfig.UseSingleBaseline;
            }
            else
            {
                root.PredictedSpawnedGhostRollbackToSpawnTick = false;
                root.RollbackPredictionOnStructuralChanges = false;
            }
            builder.AllocateString(ref root.Name, ref ghostConfig.Name);

            var serverComponents = new NativeList<ulong>(allComponents.Length, Allocator.Temp);
            var serverVariants = new NativeList<ulong>(allComponents.Length, Allocator.Temp);
            var serverSendMasks = new NativeList<int>(allComponents.Length, Allocator.Temp);
            var removeOnServer = new NativeList<GhostPrefabBlobMetaData.ComponentReference>(allComponents.Length, Allocator.Temp);
            var removeOnClient = new NativeList<GhostPrefabBlobMetaData.ComponentReference>(allComponents.Length, Allocator.Temp);
            var disableOnPredicted = new NativeList<GhostPrefabBlobMetaData.ComponentReference>(allComponents.Length, Allocator.Temp);
            var disableOnInterpolated = new NativeList<GhostPrefabBlobMetaData.ComponentReference>(allComponents.Length, Allocator.Temp);

            var removeOnServerSharedList = RemoveOnServerWorldsSharedList(rootEntity, entityManager);
            for (int i = 0; i < removeOnServerSharedList.Length; i++)
            {
                removeOnServer.Add(new GhostPrefabBlobMetaData.ComponentReference(0, TypeManager.GetTypeInfo(removeOnServerSharedList.GetTypeIndex(i)).StableTypeHash));
            }

            if (target == NetcodeConversionTarget.Server || target == NetcodeConversionTarget.ClientAndServer)
            {
                var blobRemoveOnAllServerWorldsSharedList = builder.Allocate(ref root.RemoveOnAllServerWorldsSharedList, removeOnServer.Length);

                for (int i = 0; i < removeOnServer.Length; i++)
                {
                    blobRemoveOnAllServerWorldsSharedList[i] = removeOnServer[i];
                }
            }
            else
            {
                builder.Allocate(ref root.RemoveOnAllServerWorldsSharedList, 0);
            }

            // 同时支持插值和预测客户端时，插值客户端需要禁用预测组件
            // Ghost 仅支持插值时，可以在客户端移除预测组件
            if (ghostConfig.SupportedGhostModes == GhostModeMask.All)
                disableOnInterpolated.Add(new GhostPrefabBlobMetaData.ComponentReference(0, TypeManager.GetTypeInfo(ComponentType.ReadWrite<PredictedGhost>().TypeIndex).StableTypeHash));
            else if (ghostConfig.SupportedGhostModes == GhostModeMask.Interpolated)
                removeOnClient.Add(new GhostPrefabBlobMetaData.ComponentReference(0,TypeManager.GetTypeInfo(ComponentType.ReadWrite<PredictedGhost>().TypeIndex).StableTypeHash));

            var compIdx = 0;
            var blobNumServerComponentsPerEntity = builder.Allocate(ref root.NumServerComponentsPerEntity, linkedEntities.Length);
            for (int k = 0; k < linkedEntities.Length; ++k)
            {
                int prevCount = serverComponents.Length;
                var numComponents = componentCounts[k];
                for (int i=0;i<numComponents;++i, ++compIdx)
                {
                    var comp = allComponents[compIdx];
                    var prefabType = prefabTypes[compIdx];
                    var hash = TypeManager.GetTypeInfo(comp.TypeIndex).StableTypeHash;
                    if (prefabType == GhostPrefabType.All)
                    {
                        serverComponents.Add(hash);
                        serverSendMasks.Add(sendMasksOverride[compIdx]);
                        serverVariants.Add(variants[compIdx]);
                        continue;
                    }

                    bool isCommandData = typeof(ICommandData).IsAssignableFrom(comp.GetManagedType());
                    if (isCommandData)
                    {
                        // 对会导致组件从部分 Variant 中移除的配置报告警告
                        if ((prefabType & GhostPrefabType.Server) == 0)
                            UnityEngine.Debug.LogWarning($"{ghostConfig.Name}: ICommandData {comp} is configured to be present only on the clients. Will be removed from server ghost prefab");
                        if ((prefabType & GhostPrefabType.Client) == 0)
                            UnityEngine.Debug.LogWarning($"{ghostConfig.Name}: ICommandData {comp} is configured to be present only on the server. Will be removed from from the client ghost prefab");
                        else if (prefabType == GhostPrefabType.InterpolatedClient)
                            UnityEngine.Debug.LogWarning($"{ghostConfig.Name}: ICommandData {comp} is configured to be present only on interpolated ghost. Will be removed from the server and predicted ghost prefab");
                        // 检查需要禁用的组件，并对部分配置报告警告
                        if (ghostConfig.SupportedGhostModes == GhostModeMask.All)
                        {
                            if ((prefabType & GhostPrefabType.InterpolatedClient) != 0 && (prefabType & GhostPrefabType.PredictedClient) == 0)
                                UnityEngine.Debug.LogWarning($"{ghostConfig.Name}: ICommandData {comp} is configured to be present only on interpolated ghost. Will be disabled on predicted ghost after spawning");
                        }
                    }
                    if ((prefabType & GhostPrefabType.Server) == 0)
                        removeOnServer.Add(new GhostPrefabBlobMetaData.ComponentReference(k,hash));
                    else
                    {
                        serverComponents.Add(hash);
                        serverSendMasks.Add(sendMasksOverride[compIdx]);
                        serverVariants.Add(variants[compIdx]);
                    }

                    // 移除客户端不使用的组件
                    // Ghost 仅支持预测时也要移除仅插值组件，反之亦然
                    if ((prefabType & GhostPrefabType.Client) == 0)
                        removeOnClient.Add(new GhostPrefabBlobMetaData.ComponentReference(k,hash));
                    else if (ghostConfig.SupportedGhostModes == GhostModeMask.Interpolated && (prefabType & GhostPrefabType.InterpolatedClient) == 0)
                        removeOnClient.Add(new GhostPrefabBlobMetaData.ComponentReference(k,hash));
                    else if (ghostConfig.SupportedGhostModes == GhostModeMask.Predicted && (prefabType & GhostPrefabType.PredictedClient) == 0)
                        removeOnClient.Add(new GhostPrefabBlobMetaData.ComponentReference(k,hash));

                    // Prefab 在客户端只支持单一模式时无需启用或禁用组件
                    // 前面的循环会直接从客户端移除对应组件
                    if (ghostConfig.SupportedGhostModes == GhostModeMask.All)
                    {
                        // 仅预测模式可用的组件应在插值客户端上禁用
                        if ((prefabType & GhostPrefabType.InterpolatedClient) == 0 && (prefabType & GhostPrefabType.PredictedClient) != 0)
                            disableOnInterpolated.Add(new GhostPrefabBlobMetaData.ComponentReference(k,hash));
                        if ((prefabType & GhostPrefabType.InterpolatedClient) != 0 && (prefabType & GhostPrefabType.PredictedClient) == 0)
                            disableOnPredicted.Add(new GhostPrefabBlobMetaData.ComponentReference(k,hash));
                    }
                }
                blobNumServerComponentsPerEntity[k] = serverComponents.Length - prevCount;
            }
            var blobServerComponents = builder.Allocate(ref root.ServerComponentList, serverComponents.Length);
            for (int i = 0; i < serverComponents.Length; ++i)
            {
                blobServerComponents[i].StableHash = serverComponents[i];
                blobServerComponents[i].Variant = serverVariants[i];
                blobServerComponents[i].SendMaskOverride = serverSendMasks[i];
            }

            // 即使 Prefab 本身不是 ClientServer 目标，预生成实例也可能在 ClientServer 中创建
            // 因此服务器可用内容必须记录服务器版本需要移除哪些组件
            if (target != NetcodeConversionTarget.Client)
            {
                // 仅客户端数据不需要服务器相关信息
                var blobRemoveOnServer = builder.Allocate(ref root.RemoveOnServerOnlyWorld, removeOnServer.Length);
                for (int i = 0; i < removeOnServer.Length; ++i)
                    blobRemoveOnServer[i] = removeOnServer[i];
            }
            else
                builder.Allocate(ref root.RemoveOnServerOnlyWorld, 0);
            if (target != NetcodeConversionTarget.Server)
            {
                var blobRemoveOnClient = builder.Allocate(ref root.RemoveOnClientWorlds, removeOnClient.Length);
                for (int i = 0; i < removeOnClient.Length; ++i)
                    blobRemoveOnClient[i] = removeOnClient[i];
            }
            else
                builder.Allocate(ref root.RemoveOnClientWorlds, 0);

            if (target != NetcodeConversionTarget.Server)
            {
                // 除非目标仅为服务器，否则需要插值与预测模式差异数据
                var blobDisableOnPredicted = builder.Allocate(ref root.DisableOnPredictedClient, disableOnPredicted.Length);
                for (int i = 0; i < disableOnPredicted.Length; ++i)
                    blobDisableOnPredicted[i] = disableOnPredicted[i];
                var blobDisableOnInterpolated = builder.Allocate(ref root.DisableOnInterpolatedClient, disableOnInterpolated.Length);
                for (int i = 0; i < disableOnInterpolated.Length; ++i)
                    blobDisableOnInterpolated[i] = disableOnInterpolated[i];
            }
            else
            {
                builder.Allocate(ref root.DisableOnPredictedClient, 0);
                builder.Allocate(ref root.DisableOnInterpolatedClient, 0);
            }

            return builder.CreateBlobAssetReference<GhostPrefabBlobMetaData>(Allocator.Persistent);
        }

        /// <summary>
        /// 移除未使用的组件，并添加 Ghost Prefab 上必须始终存在的组件
        /// </summary>
        /// <param name="ghostConfig">创建 Ghost Prefab 时使用的配置</param>
        /// <param name="entityManager">用于验证 <paramref name="rootEntity"/> 上存在的组件</param>
        /// <param name="rootEntity">此实体上的组件会用于配置结果，例如 <see cref="GhostOwner"/></param>
        /// <param name="ghostType">存储创建 Ghost 所用 Prefab GUID 的组件</param>
        /// <param name="linkedEntities"><paramref name="rootEntity"/> 的所有关联实体列表</param>
        /// <param name="allComponents">所有组件类型列表</param>
        /// <param name="componentCounts">每个索引对应的组件数量列表</param>
        /// <param name="target"><see cref="NetcodeConversionTarget"/></param>
        /// <param name="prefabTypes">要创建的不同 <see cref="GhostPrefabType"/> 类型列表</param>
        public static void FinalizePrefabComponents(Config ghostConfig, EntityManager entityManager,
            Entity rootEntity, GhostType ghostType, NativeArray<Entity> linkedEntities,
            NativeList<ComponentType> allComponents, NativeArray<int> componentCounts,
            NetcodeConversionTarget target, NativeArray<GhostPrefabType> prefabTypes)
        {
            var entities = new NativeArray<Entity>(allComponents.Length, Allocator.Temp);
            int compIdx = 0;
            for (int k = 0; k < linkedEntities.Length; ++k)
            {
                var numComponents = componentCounts[k];
                var ent = linkedEntities[k];
                for (int i = 0; i < numComponents; ++i, ++compIdx)
                    entities[compIdx] = ent;
            }

            // 记录应从客户端移除的所有组件，后续用于判断是否需要为客户端 Ghost 添加 DynamicSnapshotData 组件
            // 由于此判断依赖组件当前选择的序列化 Variant，提前记录可以简化后续逻辑
            var removedFromClient = new NativeArray<bool>(allComponents.Length, Allocator.Temp);

            if (target == NetcodeConversionTarget.Server)
            {
                // 转换仅服务器数据时，可以移除服务器不使用的全部组件
                for (int i=0;i< allComponents.Length;++i)
                {
                    var comp = allComponents[i];
                    var prefabType = prefabTypes[i];
                    if((prefabType & GhostPrefabType.Server) == 0)
                    {
                        entityManager.RemoveComponent(entities[i], comp);
                        if(typeof(ICommandData).IsAssignableFrom(comp.GetManagedType()))
                            UnityEngine.Debug.LogWarning($"{ghostConfig.Name}: ICommandData {comp} is configured to be present only on client ghosts. Will be removed from from the server target");
                    }
                }
            }
            else if (target == NetcodeConversionTarget.Client)
            {
                // 转换仅客户端数据时，可以移除客户端不使用的全部组件
                // Ghost 仅支持插值时，还可移除插值客户端不使用的全部组件
                // Ghost 仅支持预测时，则可移除预测客户端不使用的全部组件
                for (int i=0;i< allComponents.Length;++i)
                {
                    var comp = allComponents[i];
                    var prefabType = prefabTypes[i];
                    if (prefabType == GhostPrefabType.All)
                        continue;
                    if(typeof(ICommandData).IsAssignableFrom(comp.GetManagedType()))
                    {
                        if ((prefabType & GhostPrefabType.Client) == 0)
                            UnityEngine.Debug.LogWarning($"{ghostConfig.Name}: ICommandData {comp} is configured to be present only on the server. Will be removed from from the client target");
                        else if (ghostConfig.SupportedGhostModes == GhostModeMask.Predicted && (prefabType & GhostPrefabType.PredictedClient) == 0)
                            UnityEngine.Debug.LogWarning($"{ghostConfig.Name}: ICommandData {comp} is configured to be present only on interpolated ghost. Will be removed from the client target");
                    }

                    if ((prefabType & GhostPrefabType.Client) == 0)
                    {
                        entityManager.RemoveComponent(entities[i], comp);
                        removedFromClient[i] = true;
                    }
                    else if (ghostConfig.SupportedGhostModes == GhostModeMask.Interpolated && (prefabType & GhostPrefabType.InterpolatedClient) == 0)
                    {
                        entityManager.RemoveComponent(entities[i], comp);
                        removedFromClient[i] = true;
                    }
                    else if (ghostConfig.SupportedGhostModes == GhostModeMask.Predicted && (prefabType & GhostPrefabType.PredictedClient) == 0)
                    {
                        entityManager.RemoveComponent(entities[i], comp);
                        removedFromClient[i] = true;
                    }
                }
            }
            // 即使同时为客户端和服务器转换，如果 Ghost 始终为插值模式，也可移除仅预测客户端组件
            // 如果 Ghost 始终为预测模式，则可移除仅插值客户端组件
            else if (ghostConfig.SupportedGhostModes == GhostModeMask.Interpolated)
            {
                for (int i=0;i< allComponents.Length;++i)
                {
                    var comp = allComponents[i];
                    var prefabType = prefabTypes[i];
                    if ((prefabType & (GhostPrefabType.InterpolatedClient | GhostPrefabType.Server)) == 0)
                    {
                        entityManager.RemoveComponent(entities[i], comp);
                        removedFromClient[i] = true;
                    }
                }
            }
            else if (ghostConfig.SupportedGhostModes == GhostModeMask.Predicted)
            {
                for (int i=0;i< allComponents.Length;++i)
                {
                    var comp = allComponents[i];
                    var prefabType = prefabTypes[i];
                    if ((prefabType & (GhostPrefabType.PredictedClient | GhostPrefabType.Server)) == 0)
                    {
                        entityManager.RemoveComponent(entities[i], comp);
                        removedFromClient[i] = true;
                        if(typeof(ICommandData).IsAssignableFrom(comp.GetManagedType()))
                            UnityEngine.Debug.LogWarning($"{ghostConfig.Name}: ICommandData {comp} is configured to be present only on interpolated ghost. Will be removed from the client and server target");
                    }
                }
            }
            else
            {
                for (int i=0;i< allComponents.Length;++i)
                {
                    var comp = allComponents[i];
                    var prefabType = prefabTypes[i];
                    if (prefabType == 0)
                    {
                        entityManager.RemoveComponent(entities[i], comp);
                        removedFromClient[i] = true;
                    }
                }
            }

            entityManager.AddComponentData(rootEntity, ghostType);

            // FIXME：组件裁剪或许应由独立系统在此之前执行，以便任何修改都能触发重新转换并避免反射

            // 必须添加 Shared Ghost Type，确保具有相同 Archetype 的不同 Ghost 类型进入不同 Chunk
            // 原因是相同 Archetype 的 Ghost 可能使用不同序列化规则，而大部分按 Chunk 执行的逻辑
            // 都假设 Chunk 内 Ghost 在序列化意义上属于同一类型
            entityManager.AddSharedComponent(rootEntity, new GhostTypePartition {SharedValue = ghostType});

            // 所有类型都具有 Ghost 基础组件
            entityManager.AddComponentData(rootEntity, new GhostInstance());
            // 数据仅供客户端使用且 Ghost 只支持插值时，无需添加预测 Ghost 组件
            if (target != NetcodeConversionTarget.Client || ghostConfig.SupportedGhostModes != GhostModeMask.Interpolated)
                entityManager.AddComponentData(rootEntity, new PredictedGhost());
            if (ghostConfig.UsePreSerialization)
                entityManager.AddComponentData(rootEntity, default(PreSerializedGhost));

            var hasBuffers = false;
            // 检查实体是否仍有 Buffer，并为客户端添加 SnapshotDynamicData Buffer，服务器必须将其移除
            if (target != NetcodeConversionTarget.Server)
            {
                // Prefab 不支持任何客户端模式，即仅服务器时，无需添加动态 Buffer Snapshot
                // 此处必须考虑 Variant 序列化，因此使用 removedFromClient 结果
                for (int i = 0; i < allComponents.Length && !hasBuffers; ++i)
                    hasBuffers |= (allComponents[i].IsBuffer && !removedFromClient[i]) && (prefabTypes[i] & GhostPrefabType.Client) != 0;
                // 转换目标为客户端或客户端与服务器时添加，后者会在运行时从服务器移除
                entityManager.AddComponentData(rootEntity, new SnapshotData());
                entityManager.AddBuffer<SnapshotDataBuffer>(rootEntity);
                if(hasBuffers)
                    entityManager.AddBuffer<SnapshotDynamicDataBuffer>(rootEntity);
            }

        }

        // 获取所有非烘焙类型的组件
        private static NativeArray<ComponentType> GetNotBakingComponentTypes(EntityManager entityManager, Entity entity, ComponentType linkedEntityGroupComponentType)
        {
            var components = entityManager.GetComponentTypes(entity);
            NativeList<ComponentType> relevantComponents = new NativeList<ComponentType>(components.Length, Allocator.Temp);

            // 移除所有烘焙组件
            for (int index = 0; index < components.Length; ++index)
            {
                if ((components[index].TypeIndex & (TypeManager.BakingOnlyTypeFlag | TypeManager.TemporaryBakingTypeFlag)) == 0
                    && (components[index] != linkedEntityGroupComponentType))
                {
                    // 忽略仅用于烘焙的类型
                    relevantComponents.Add(components[index]);
                }
            }
            return relevantComponents.AsArray();
        }

        /// <summary>
        /// 构建 Ghost Prefab 所有子实体上全部组件类型列表的辅助方法，不应直接调用
        /// </summary>
        /// <param name="entityManager">用于向 Ghost 子实体添加组件数据</param>
        /// <param name="linkedEntities">关联实体，索引 0 为根实体，之后为其子实体，每个子实体都会标记 <see cref="GhostChildEntity"/></param>
        /// <param name="allComponents">填充根实体和子实体的组件</param>
        /// <param name="componentCounts">填充每个 Ghost 的组件数量</param>
        public static void CollectAllComponents(EntityManager entityManager, NativeArray<Entity> linkedEntities, out NativeList<ComponentType> allComponents, out NativeArray<int> componentCounts)
        {
            var linkedEntityGroupComponentType = ComponentType.ReadWrite<LinkedEntityGroup>();

            var rootComponents = GetNotBakingComponentTypes(entityManager, linkedEntities[0], linkedEntityGroupComponentType);
            rootComponents.Sort(default(ComponentHashComparer));
            // 收集层级中的全部组件
            allComponents = new NativeList<ComponentType>(rootComponents.Length*linkedEntities.Length, Allocator.Temp);
            componentCounts = new NativeArray<int>(linkedEntities.Length, Allocator.Temp);
            allComponents.AddRange(rootComponents);
            componentCounts[0] = rootComponents.Length;

            // 将所有子实体标记为 Ghost 子实体，索引 0 是根实体，不应具有 GhostChildEntity
            for (int i = 1; i < linkedEntities.Length; ++i)
            {
                entityManager.AddComponentData(linkedEntities[i], default(GhostChildEntity));
                var childComponents = GetNotBakingComponentTypes(entityManager, linkedEntities[i], linkedEntityGroupComponentType);
                childComponents.Sort(default(ComponentHashComparer));
                allComponents.AddRange(childComponents);
                componentCounts[i] = childComponents.Length;
            }
        }

        /// <summary>
        /// 将实体转换为 Ghost Prefab 并注册到集合
        /// 如果 Prefab 和 LinkedEntityGroup 组件尚不存在，此方法会添加它们
        /// 方法还会添加 Prefab 用作 Ghost 所需的全部组件，并向 GhostCollectionSystem 注册
        /// 转换 Ghost Prefab 时创建的 Blob Asset 由 GhostCollectionSystem 持有并负责释放
        /// 因此调用方不应释放该 Blob Asset
        /// 客户端和服务器必须以完全相同的方式创建 Prefab，并且 Prefab 必须包含全部组件
        /// 如需部分组件仅存在于服务器或客户端，请使用组件覆盖项
        /// </summary>
        /// <remarks>
        /// 在系统 OnCreate 方法中调用时，必须确保该系统在 DefaultVariantSystemGroup 之后创建
        /// 因为访问序列化策略前必须先完成注册
        /// </remarks>
        /// <param name="entityManager">用于向 Ghost 子实体添加组件数据</param>
        /// <param name="prefab">要转换的实体 Prefab</param>
        /// <param name="config">创建 Ghost Prefab 时使用的配置</param>
        /// <param name="overrides">特定组件的覆盖类型</param>
        public static void ConvertToGhostPrefab(EntityManager entityManager, Entity prefab,
            Config config,
            NativeParallelHashMap<Component, ComponentOverride> overrides = default)
        {
            // 确保存在有效的覆盖项映射，以简化后续逻辑
            if (!overrides.IsCreated)
                overrides = new NativeParallelHashMap<Component, ComponentOverride>(1, Allocator.Temp);

#if !DOTS_DISABLE_DEBUG_NAMES
            entityManager.GetName(prefab, out var name);
            if(name.IsEmpty)
                entityManager.SetName(prefab, config.Name);
#endif

            // 子实体也必须添加 Prefab 标签
            if (!entityManager.HasComponent<LinkedEntityGroup>(prefab))
            {
                var buffer = entityManager.AddBuffer<LinkedEntityGroup>(prefab);
                buffer.Add(prefab);
            }
            var linkedEntityBuffer = entityManager.GetBuffer<LinkedEntityGroup>(prefab);
            var linkedEntitiesArray = new NativeArray<Entity>(linkedEntityBuffer.Length, Allocator.Temp);
            for (int i = 0; i < linkedEntityBuffer.Length; ++i)
                linkedEntitiesArray[i] = linkedEntityBuffer[i].Value;
            // 第二轮再添加组件，以避免使 Buffer 安全句柄失效
            for (int i = 0; i < linkedEntitiesArray.Length; ++i)
                entityManager.AddComponent<Prefab>(linkedEntitiesArray[i]);

            var allComponents = default(NativeList<ComponentType>);
            var componentCounts = default(NativeArray<int>);
            if (!config.CollectComponentFunc.Ptr.IsCreated)
            {
                CollectAllComponents(entityManager, linkedEntitiesArray, out allComponents, out componentCounts);
            }
            else
            {
                allComponents = new NativeList<ComponentType>(256, Allocator.Temp);
                componentCounts = new NativeArray<int>(linkedEntitiesArray.Length, Allocator.Temp);
                config.CollectComponentFunc.Ptr.Invoke(GhostComponentSerializer.IntPtrCast(ref allComponents), GhostComponentSerializer.IntPtrCast(ref componentCounts));
            }

            var prefabTypes = new NativeArray<GhostPrefabType>(allComponents.Length, Allocator.Temp);
            var sendMasksOverride = new NativeArray<int>(allComponents.Length, Allocator.Temp);
            var variants = new NativeArray<ulong>(allComponents.Length, Allocator.Temp);

            // TODO：考虑修改 API，将此数据作为参数传入
            using var collectionDataQuery = entityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<GhostComponentSerializerCollectionData>());
            var collectionData = collectionDataQuery.GetSingleton<GhostComponentSerializerCollectionData>();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            {
                entityManager.GetName(prefab, out var prefabName);
                collectionData.ThrowIfCollectionNotFinalized($"ConvertToGhostPrefab on prefab '{prefab.ToFixedString()} ({prefabName})'");
            }
#endif

            int childIndex = 0;
            int childStart = 0;
            for (int i = 0; i < allComponents.Length; ++i)
            {
                while (i - childStart == componentCounts[childIndex])
                {
                    ++childIndex;
                    childStart = i;
                }
                var hasOverrides = overrides.TryGetValue(new Component{ComponentType = allComponents[i], ChildIndex = childIndex}, out var compOverride);
                ulong variant = 0;
                if (hasOverrides && (compOverride.OverrideType & ComponentOverrideType.Variant) != 0)
                    variant = compOverride.Variant;

                var variantType = collectionData.GetCurrentSerializationStrategyForComponent(allComponents[i], variant, childIndex == 0);
                prefabTypes[i] = variantType.PrefabType;
                sendMasksOverride[i] = -1;
                variants[i] = variantType.Hash;
                if (hasOverrides)
                {
                    if ((compOverride.OverrideType & ComponentOverrideType.PrefabType) != 0)
                        prefabTypes[i] = compOverride.PrefabType;
                    if ((compOverride.OverrideType & ComponentOverrideType.SendMask) != 0)
                        sendMasksOverride[i] = (int)compOverride.SendMask;
                }
            }

            NetcodeConversionTarget target = (entityManager.World.IsServer()) ? NetcodeConversionTarget.Server : NetcodeConversionTarget.Client;
            // 以此 C# 文件的 GUID 为命名空间、Prefab 名称为名称计算 UUID v5，详情参见 RFC 4122
            // TODO：这里或许应使用命名空间 GUID 与名称拼接后的原始字节
            GhostType ghostType;
            if (config.UUID5GhostType != default)
            {
                ghostType = GhostType.FromHash128(config.UUID5GhostType);
#if NETCODE_DEBUG || UNITY_EDITOR
                if (!ghostType.ValidateIsUUID5())
                    throw new InvalidOperationException($"The custom UUID5 ghost type {config.UUID5GhostType} is not a valid UUID5 compliant unique identifier. Please refer to https://datatracker.ietf.org/doc/html/rfc4122 for more details");
#endif
            }
            else
            {
                var uuid5 = new SHA1($"f17641b8-279a-94b1-1b84-487e72d49ab5{config.Name}");
                // 使用命名空间与 Ghost 名称生成 UUID5，获得不会与已加载 Prefab 冲突的唯一标识符
                ghostType = GhostType.FromHash128(ConvertHash128ToUUID5(uuid5.ToHash128()));
            }
            // 此组件应仅存在于 Prefab
            // FinalizePrefabComponents 也会为非 Prefab 实体调用，因此不能在那里添加
            if (target != NetcodeConversionTarget.Server && config.SupportedGhostModes != GhostModeMask.Interpolated)
            {
                entityManager.AddComponent<PredictedGhostSpawnRequest>(prefab);
                entityManager.SetComponentEnabled<PredictedGhostSpawnRequest>(prefab, false);
            }


            FinalizePrefabComponents(config, entityManager, prefab, ghostType, linkedEntitiesArray,
                        allComponents, componentCounts, target, prefabTypes);

            using var codePrefabQuery = entityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<CodeGhostPrefab>());
            if (!codePrefabQuery.TryGetSingletonEntity<CodeGhostPrefab>(out var codePrefabSingleton))
                codePrefabSingleton = entityManager.CreateSingletonBuffer<CodeGhostPrefab>();
            var codePrefabs = entityManager.GetBuffer<CodeGhostPrefab>(codePrefabSingleton);

#if NETCODE_DEBUG
            for (int i = 0; i < codePrefabs.Length; ++i)
            {
                if (entityManager.GetComponentData<GhostType>(codePrefabs[i].entity) == ghostType)
                {
                    throw new InvalidOperationException("Duplicate ghost prefab found, all ghost prefabs must have a unique name");
                }
            }
            #endif

            var blobAsset = CreateBlobAsset(config, entityManager, prefab, linkedEntitiesArray,
                allComponents, componentCounts, target, prefabTypes, sendMasksOverride, variants);
            codePrefabs.Add(new CodeGhostPrefab{entity = prefab, blob = blobAsset});
            entityManager.AddComponentData(prefab, new GhostPrefabMetaData
            {
                Value = blobAsset
            });
        }
    }
}
