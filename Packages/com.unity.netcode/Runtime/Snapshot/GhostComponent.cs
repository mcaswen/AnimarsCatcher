using Unity.Entities;
using Unity.Collections;
using System;
using Unity.Burst;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于升级到新组件类型的临时类型，将在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("GhostComponent has been deprecated. Use GhostInstance instead (UnityUpgradable) -> GhostInstance", true)]
    [DontSupportPrefabOverrides]
    public struct GhostComponent : IComponentData
    {
    }
    /// <summary>
    /// 用于升级到新组件类型的临时类型，将在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("GhostChildEntityComponent has been deprecated. Use GhostChildEntity instead (UnityUpgradable) -> GhostChildEntity", true)]
    [DontSupportPrefabOverrides]
    public struct GhostChildEntityComponent : IComponentData
    {
    }
    /// <summary>
    /// 用于支持升级的临时类型，将在 1.0 版本前移除
    /// </summary>
    [Obsolete("GhostTypeComponent has been deprecated. Use GhostType instead (UnityUpgradable) -> GhostType", true)]
    [DontSupportPrefabOverrides]
    public struct GhostTypeComponent : IComponentData
    {
    }
    /// <summary>
    /// 用于升级到新组件类型的临时类型，将在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("SharedGhostTypeComponent has been deprecated. Use GhostTypePartition instead (UnityUpgradable) -> GhostTypePartition", true)]
    public struct SharedGhostTypeComponent : IComponentData
    {
        /// <summary>
        /// 此实体使用的 Ghost 类型
        /// </summary>
        public GhostType SharedValue;
    }
    /// <summary>
    /// 用于升级到新组件类型的临时类型，将在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("PredictedGhostComponent has been deprecated. Use PredictedGhost instead (UnityUpgradable) -> PredictedGhost", true)]
    [DontSupportPrefabOverrides]
    public struct PredictedGhostComponent : IComponentData
    {
    }
    /// <summary>
    /// 用于升级到新组件类型的临时类型，将在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("PredictedGhostSpawnRequestComponent has been deprecated. Use PredictedGhostSpawnRequest instead (UnityUpgradable) -> PredictedGhostSpawnRequest", true)]
    public struct PredictedGhostSpawnRequestComponent : IComponentData
    {
    }
    /// <summary>
    /// 用于升级到新组件类型的临时类型，将在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("PendingSpawnPlaceholderComponent has been deprecated. Use PendingSpawnPlaceholder instead (UnityUpgradable) -> PendingSpawnPlaceholder", true)]
    public struct PendingSpawnPlaceholderComponent : IComponentData
    {
    }

    /// <summary>
    /// 标识通过网络复制的实体
    /// </summary>
    [DontSupportPrefabOverrides]
    public struct GhostInstance : IComponentData, IEquatable<GhostInstance>
    {
        /// <summary>
        /// 服务器分配给 Ghost 的 ID，Ghost 被销毁后其 ID 会被回收并可分配给新的 Ghost
        /// 因此不能仅使用 Ghost ID 作为唯一标识符
        /// <see cref="ghostId"/> 与 <see cref="spawnTick"/> 的组合则保证唯一，因为在任意时刻
        /// 只能存在一个在指定 Tick 生成且具有给定 ID 的 Ghost
        /// </summary>
        public int ghostId;
        /// <summary>
        /// Ghost Prefab 类型，即其在 Ghost Prefab 集合中的索引
        /// </summary>
        public int ghostType;
        /// <summary>
        /// 实体在服务器上生成时的 Tick，与 <see cref="ghostId"/> 组合后保证始终唯一
        /// </summary>
        public NetworkTick spawnTick;

        /// <summary>
        /// 将 GhostInstance 隐式转换为 <see cref="SpawnedGhost"/> 实例
        /// </summary>
        /// <param name="comp">要转换的 Ghost 组件</param>
        /// <returns>由 Ghost 组件转换得到的 <see cref="SpawnedGhost"/></returns>
        public static implicit operator SpawnedGhost(in GhostInstance comp)
        {
            return new SpawnedGhost(comp.ghostId, comp.spawnTick);
        }

        /// <summary>
        /// 返回包含各字段值且便于阅读的 GhostInstance FixedString
        /// </summary>
        /// <returns>包含各字段值且便于阅读的 GhostInstance FixedString</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString128Bytes ToFixedString()
        {
            return $"GhostInst[type:{ghostType}|id:{ghostId},st:{spawnTick.ToFixedString()}]";
        }

        /// <inheritdoc cref="object.Equals(object)"/>
        public static bool operator ==(GhostInstance left, GhostInstance right) => left.Equals(right);

        /// <inheritdoc cref="object.Equals(object)"/>
        public static bool operator !=(GhostInstance left, GhostInstance right) => !left.Equals(right);

        /// <inheritdoc cref="object.Equals(object)"/>
        public bool Equals(GhostInstance other) => ghostId == other.ghostId && ghostType == other.ghostType && spawnTick.Equals(other.spawnTick);

        /// <inheritdoc cref="object.Equals(object)"/>
        public override bool Equals(object obj) => obj is GhostInstance other && Equals(other);

        /// <inheritdoc cref="object.GetHashCode"/>
        public override int GetHashCode() => HashCode.Combine(ghostId, ghostType, spawnTick);

        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }

    /// <summary>
    /// 添加到多实体 Ghost 中子实体上的标签，如果组内 Ghost 不是该组的根，也应添加此标签
    /// </summary>
    [DontSupportPrefabOverrides]
    public struct GhostChildEntity : IComponentData
    {}

    /// <summary>
    /// 存储创建该 Ghost 所用 Prefab 的 GUID，即使两个 Ghost 具有相同 Archetype，也能通过它可靠地查找 Ghost 类型
    /// </summary>
    [DontSupportPrefabOverrides]
    [Serializable]
    public struct GhostType : IComponentData,
        IEquatable<GhostType>
    {
        /// <summary>
        /// Prefab GUID 的前 4 个字节
        /// </summary>
        [UnityEngine.SerializeField]
        internal uint guid0;
        /// <summary>
        /// Prefab GUID 的第 2 组 4 字节
        /// </summary>
        [UnityEngine.SerializeField]
        internal uint guid1;
        /// <summary>
        /// Prefab GUID 的第 3 组 4 字节
        /// </summary>
        [UnityEngine.SerializeField]
        internal uint guid2;
        /// <summary>
        /// Prefab GUID 的第 4 组 4 字节
        /// </summary>
        [UnityEngine.SerializeField]
        internal uint guid3;

        /// <summary>
        /// 根据 <see cref="Hash128"/> GUID 字符串构造新的 <see cref="GhostType"/>
        /// </summary>
        /// <param name="guid">GUID 字符串，Hash128 或 Unity.Engine.GUID 字符串均有效</param>
        /// <returns>新的 GhostType 实例</returns>
        [BurstDiscard]
        internal static GhostType FromHash128String(string guid)
        {
            var hash = new Hash128(guid);
            return new GhostType
            {
                guid0 = hash.Value.x,
                guid1 = hash.Value.y,
                guid2 = hash.Value.z,
                guid3 = hash.Value.w,
            };
        }

        /// <summary>
        /// 根据给定的 <see cref="Hash128"/> GUID 创建新的 <see cref="GhostType"/>
        /// </summary>
        /// <param name="guid">源 GUID</param>
        /// <returns>由给定 <see cref="Hash128"/> GUID 转换得到的 Ghost 类型</returns>
        internal static GhostType FromHash128(Hash128 guid)
        {
            return new GhostType
            {
                guid0 = guid.Value.x,
                guid1 = guid.Value.y,
                guid2 = guid.Value.z,
                guid3 = guid.Value.w,
            };
        }

        /// <summary>
        /// 将 <see cref="GhostType"/> 转换为 <see cref="Hash128"/> 实例，该 Hash 始终与创建 Ghost 所用 Prefab 的 GUID 一致
        /// </summary>
        /// <param name="ghostType">要转换的 Ghost 类型</param>
        /// <returns>转换为 <see cref="Hash128"/> 的 Ghost 类型</returns>
        public static explicit operator Hash128(GhostType ghostType)
        {
            return new Hash128(ghostType.guid0, ghostType.guid1, ghostType.guid2, ghostType.guid3);

        }

        /// <summary>
        /// 返回两个 GhostType 是否相同
        /// </summary>
        /// <param name="lhs">左侧 Ghost 类型</param>
        /// <param name="rhs">右侧 Ghost 类型</param>
        /// <returns>两个类型的 GUID 是否相同</returns>
        public static bool operator ==(GhostType lhs, GhostType rhs)
        {
            return lhs.guid0 == rhs.guid0 && lhs.guid1 == rhs.guid1 && lhs.guid2 == rhs.guid2 && lhs.guid3 == rhs.guid3;
        }
        /// <summary>
        /// 返回两个 GhostType 是否不同
        /// </summary>
        /// <param name="lhs">左侧 Ghost 类型</param>
        /// <param name="rhs">右侧 Ghost 类型</param>
        /// <returns>两个类型的 GUID 是否相同</returns>
        public static bool operator !=(GhostType lhs, GhostType rhs)
        {
            return lhs.guid0 != rhs.guid0 || lhs.guid1 != rhs.guid1 || lhs.guid2 != rhs.guid2 || lhs.guid3 != rhs.guid3;
        }
        /// <summary>
        /// 返回 <see cref="other"/> 是否与当前实例相同
        /// </summary>
        /// <param name="other">Ghost 类型引用</param>
        /// <returns><see cref="other"/> 是否与当前实例相同</returns>
        public bool Equals(GhostType other)
        {
            return this == other;
        }
        /// <summary>
        /// 返回 <see cref="obj"/> 引用是否为 GhostType 类型，以及它是否与当前实例相同
        /// </summary>
        /// <param name="obj">Ghost 类型引用</param>
        /// <returns>与传入的 GhostType 相同时为 true</returns>
        public override bool Equals(object obj)
        {
            if(obj is GhostType aGT) return Equals(aGT);
            return false;
        }

        /// <summary>
        /// 返回适合将组件插入字典或有序容器的 Hash Code
        /// </summary>
        /// <returns>当前实例的 Hash Code</returns>
        public override int GetHashCode()
        {
            var result = guid0.GetHashCode();
            result = (result*31) ^ guid1.GetHashCode();
            result = (result*31) ^ guid2.GetHashCode();
            result = (result*31) ^ guid3.GetHashCode();
            return result;
        }
    }


    /// <summary>
    /// 服务器用于确保不同 Ghost 类型位于不同 Chunk 的组件
    /// 即使它们具有相同 Archetype 也如此，与组件数据无关
    /// </summary>
    [DontSupportPrefabOverrides]
    public struct GhostTypePartition : ISharedComponentData
    {
        /// <summary>
        /// 此实体使用的 Ghost 类型
        /// </summary>
        public GhostType SharedValue;
    }



    /// <summary>
    /// 在客户端标识实体采用预测模式而非插值模式的组件
    /// </summary>
    /// <seealso cref="GhostMode"/>
    /// <seealso cref="GhostModeMask"/>
    [DontSupportPrefabOverrides]
    public struct PredictedGhost : IComponentData
    {
        /// <summary>
        /// 已应用到该实体的最后一个服务器 Snapshot
        /// </summary>
        public NetworkTick AppliedTick;
        /// <summary>
        /// <para>实体应开始预测的服务器 Tick</para>
        /// <para>收到新的 Ghost Snapshot 时，实体会同步到服务器状态
        /// 并将 PredictionStartTick 设为该 Snapshot 的服务器 Tick</para>
        /// <para>否则，PredictionStartTick 应对应以下 Tick</para>
        /// <para>如果存在预测备份，参见 <see cref="GhostPredictionHistoryState"/>，则为客户端最后完成模拟的完整 Tick，参见 <see cref="ClientServerTickRate"/></para>
        /// <para>如果找不到连续备份，则为最后收到的 Snapshot Tick</para>
        /// </summary>
        public NetworkTick PredictionStartTick;

        /// <summary>
        /// 查询实体是否应在给定 Tick 进行模拟预测
        /// </summary>
        /// <param name="tick">要模拟的网络 Tick</param>
        /// <returns>实体应进行模拟时为 true</returns>
        public bool ShouldPredict(NetworkTick tick)
        {
            return !PredictionStartTick.IsValid || tick.IsNewerThan(PredictionStartTick);
        }
    }

    /// <summary>
    /// <para>
    /// 客户端用于请求预测生成 Ghost 的可选组件
    /// 满足以下条件时，该组件会自动添加到制作的 Ghost Prefab 上<br/>
    /// - 烘焙目标为 <see cref="NetcodeConversionTarget.Client"/> 或 <see cref="NetcodeConversionTarget.ClientAndServer"/><br/>
    /// - 使用混合 Authoring 工作流，且 <see cref="GhostAuthoringComponent.SuypportedGhostModes"/> 为 <see cref="GhostModeMask.Predicted"/> 或 <see cref="GhostModeMask.All"/><br/>
    /// - 使用 <see cref="GhostPrefabCreation.ConvertToGhostPrefab"/>，且 <see cref="GhostPrefabCreation.Config.SupportedGhostModes"/> 设为 <see cref="GhostModeMask.Predicted"/> 或 <see cref="GhostModeMask.All"/><br/>
    /// </para>
    /// <para>
    /// 该组件的启用状态初始化为禁用，因此 WithAll 等查询不会找到此组件
    /// 如果需要检查该组件是否存在，这种需求很少见，请改用 WithDisabled 或 WithPresent
    /// </para>
    /// <para>
    /// <see cref="PredictedGhostSpawnSystem"/> 负责消费请求，并使用 Ghost 当前状态及其生成 Tick 初始化 Ghost Snapshot Buffer
    /// <list type="bullet">
    /// 此初始化过程包括
    /// <item>Ghost 初始化后，将组件启用状态改为 Enabled</item>
    /// <item>在 BeginSimulationCommandBufferSystem 中安排移除该组件，并于下一帧执行</item>
    /// </list>
    /// 该组件会在下一帧开始时被移除，在此之前暂时将其启用，以避免多次重新初始化 Ghost 状态
    /// 这是因为 PredictedGhostSpawnSystem 也会在预测循环中更新，参见 <see cref="PredictedSpawningSystemGroup"/>，每帧可能更新多次
    ///</para>
    /// <para>
    /// 此包通过 <see cref="DefaultGhostSpawnClassificationSystem"/> 提供预测生成的默认处理方式
    /// 如果需要以自定义或更精确的方式，将预测生成实体与服务器权威生成实体进行匹配
    /// 可以实现自定义生成分类系统，详情参见 <see cref="GhostSpawnClassificationSystem"/>
    /// </para>
    /// </summary>
    public struct PredictedGhostSpawnRequest : IComponentData, IEnableableComponent
    {
    }

    /// <summary>
    /// 在客户端标识实体是尚未生成 Ghost 的占位符组件
    /// 即该实体还不是一个真正的 Ghost
    /// </summary>
    /// <remarks>
    /// 注意：查询 <see cref="GhostInstance"/> 时如果未排除此组件，查询会返回占位 Ghost，除非手动排除
    /// </remarks>
    public struct PendingSpawnPlaceholder : IComponentData
    {
    }

    /// <summary>
    /// 用于处理 Ghost 组件的工具方法
    /// </summary>
    public static class GhostComponentUtilities
    {
        /// <summary>
        /// 在 Ghost 组件数组中查找第一个有效的 Ghost 类型 ID
        /// 预生成 Ghost 的类型 ID 为 -1
        /// </summary>
        /// <param name="self">包含 Ghost 类型 ID 的 NativeArray</param>
        /// <returns>找到有效类型的 Ghost 时返回其类型索引，否则返回 -1</returns>
        public static int GetFirstGhostTypeId(this NativeArray<GhostInstance> self)
        {
            return self.GetFirstGhostTypeId(out _);
        }

        /// <summary>
        /// 在 Ghost 组件数组中查找第一个有效的 Ghost 类型 ID
        /// 预生成 Ghost 的类型 ID 为 -1
        /// 未找到 Ghost 类型 ID 时返回 -1
        /// </summary>
        /// <param name="self">包含 Ghost 类型 ID 的 NativeArray</param>
        /// <param name="firstGhost">用于存储找到的第一个有效 Ghost 类型索引</param>
        /// <returns>有效的 Ghost 类型 ID，未找到时返回 -1</returns>
        public static int GetFirstGhostTypeId(this NativeArray<GhostInstance> self, out int firstGhost)
        {
            firstGhost = 0;
            int ghostTypeId = self[0].ghostType;
            while (ghostTypeId == -1 && ++firstGhost < self.Length)
            {
                ghostTypeId = self[firstGhost].ghostType;
            }
            return ghostTypeId;
        }

        /// <summary>
        /// 以 <see cref="NativeText"/> 获取组件名称，此方法兼容 Burst
        /// </summary>
        /// <param name="self">要获取名称的组件类型</param>
        /// <returns>组件名称</returns>
        public static NativeText.ReadOnly GetDebugTypeName(this ComponentType self)
        {
            return TypeManager.GetTypeInfo(self.TypeIndex).DebugTypeName;
        }
    }

    /// <summary>
    /// 在服务器实例化 Ghost 时设置此组件后，初始化 GhostInstance 组件将使用这里的 Ghost ID 和生成 Tick
    /// 而不是采用当前服务器 Tick 与可用最大 Ghost ID 的常规方式
    /// </summary>
    internal struct OverrideGhostData : IComponentData
    {
        public int GhostId;
        public NetworkTick SpawnTick;
    }
}
