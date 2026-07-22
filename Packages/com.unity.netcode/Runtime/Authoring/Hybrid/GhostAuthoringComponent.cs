using System;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Unity.Entities.Hybrid.Baking;
using Unity.NetCode.Hybrid;
using UnityEngine.Serialization;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>GhostAuthoringComponent 是配置和创建可复制 Ghost 类型的主要入口
    /// 此组件只能添加到 GameObject 层级根节点</para>
    /// <para>它用于设置 Ghost 的全部属性，
    /// 例如复制模式 <see cref="SupportedGhostModes"/>、带宽优化策略 <see cref="OptimizationMode"/>、
    /// Ghost 的 <see cref="Importance"/>（发送频率）等</para>
    /// </summary>
    /// <seealso cref="GhostAuthoringInspectionComponent"/>
    [RequireComponent(typeof(LinkedEntityGroupAuthoring))]
    [DisallowMultipleComponent]
    [HelpURL(Authoring.HelpURLs.GhostAuthoringComponent)]
    public class GhostAuthoringComponent : MonoBehaviour
    {
#if UNITY_EDITOR
    void OnValidate()
    {
        string assetPath = null;
        if (UnityEditor.EditorUtility.IsPersistent(gameObject))  // 与 gameObject.scene.IsValid() 相比，这是检查对象是否属于 Asset 的更快方式
        {
            assetPath = UnityEditor.AssetDatabase.GetAssetPath(gameObject);
        }
        else
        {
            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(gameObject);
            if (prefabStage != null)
                assetPath = prefabStage.assetPath;
        }

        if (!string.IsNullOrEmpty(assetPath))
        {
            var guid = UnityEditor.AssetDatabase.AssetPathToGUID(assetPath);
            prefabId = guid;
        }
    }
#endif

        /// <summary>
        /// 强制 Ghost Baker 将此 GameObject 视为 Prefab
        /// 适用于通过代码把 Ghost Prefab 创建为 GameObject，再使用 ConvertGameObjectHierarchy 转换为 Entity Prefab 的情况
        /// </summary>
        [NonSerialized] public bool ForcePrefabConversion;

        /// <summary>
        /// 未通过 GhostSpawnClassificationSystem 手动修改时使用的 Ghost 模式
        /// 如果设为 OwnerPredicted，则拥有该 Ghost 的客户端会对其进行预测，其他客户端会对其进行插值
        /// 使用所有者预测时，不得通过分类系统修改该模式
        /// </summary>
        [Tooltip("The `GhostMode` used when first spawned (assuming you do not manually change it, using a GhostSpawnClassificationSystem).\n\nIf set to 'Owner Predicted', the ghost will be 'Predicted' on the client which owns it, and 'Interpolated' on all others. If using 'Owner Predicted', you cannot change the ghost mode via a classification system.")]
        public GhostMode DefaultGhostMode = GhostMode.Interpolated;
        /// <summary>
        /// 此 Ghost 支持的 Ghost 模式，可在创作阶段执行更多优化，但会导致运行时无法更改 Ghost 模式
        /// </summary>
        [Tooltip("Every `GhostMode` supported by this ghost. Setting this to either 'Predicted' or 'Interpolated' will allow NetCode to perform some more optimizations at authoring time. However, it makes it impossible to change ghost mode at runtime.")]
        public GhostModeMask SupportedGhostModes = GhostModeMask.All;
        /// <summary>
        /// 此设置只用于优化，无论如何设置，Ghost 修改后都会发送
        /// 静态优化会使状态变化时的 Snapshot 略大，但在状态未变化时使 Snapshot 更小
        /// </summary>
        [Tooltip("Bandwidth and CPU optimization:\n\n - <b>Static</b> - This ghost will only be added to a snapshot when its ghost values actually change.\n<i>Examples: Barrels, trees, dropped items, asteroids etc.</i>\n\n - <b>Dynamic</b> - This ghost will be replicated at a regular interval, regardless of whether or not its values have changed, allowing for more aggressive compression.\n<i>Examples: Character controllers, missiles, important gameplay items like CTF flags and footballs etc.</i>\n\n<i>Marking a ghost as `Static` makes snapshots slightly larger when replicated values change, but smaller when they do not.</i>")]
        public GhostOptimizationMode OptimizationMode = GhostOptimizationMode.Dynamic;
        /// <summary>
        /// 如果一个 Snapshot 无法容纳所有 Ghost，则只发送最重要的 Ghost，重要度越高越可能被发送
        /// </summary>
        [Tooltip(@"<b>Importance</b> determines how ghost chunks are prioritized against each other when working out what to send in the upcoming snapshot. Higher values are sent more frequently. Applied at the chunk level.
<i>Simplified example: When comparing a gameplay-critical <b>Player</b> ghost with an <b>Importance</b> of 100 to a cosmetic <b>Cone</b> ghost with an <b>Importance</b> of 1, the <b>Player</b> ghost will likely be sent 100 times for every 1 time the <b>Cone</b> will be.</i>")]
        [Min(1)]
        public int Importance = 1;

        /// <summary>
        ///     此 Ghost Prefab 类型的 Ghost Chunk 理论最大发送频率，单位为 Hz，少数特殊情况除外
        ///     重要提示：MaxSendRate 只表示可能达到的最大复制频率，无法保证在所有情况下强制达到
        ///     最终实时发送频率还取决于 <see cref="ClientServerTickRate.NetworkTickRate"/>、Ghost 实例数量、
        ///     <see cref="Importance"/>、重要度缩放、<see cref="GhostSendSystemData.DefaultSnapshotPacketSize"/>、结构变化等因素
        /// </summary>
        /// <remarks>
        /// 可使用此设置直接降低对带宽影响最大的 Ghost 类型的带宽消耗
        /// 注意：预测 Ghost 尤其容易受到影响，因为较低的值会降低回滚和重模拟频率
        /// 预测 Ghost 只有在收到新数据后才会回滚和重模拟，因此总体上可以节省客户端 CPU 时间
        /// 但这也可能造成更大的客户端误预测误差，进而需要更大幅度的修正
        /// </remarks>
        [Tooltip(@"The <b>theoretical</b> maximum send frequency (in <b>Hertz</b>) for ghost chunks of this ghost prefab type.

<b>Important Note:</b> The <b>MaxSendRate</b> only denotes the maximum possible replication frequency. Other factors (like <b>NetworkTickRate</b>, ghost instance count, <b>Importance</b>, <b>Importance-Scaling</b>, <b>DefaultSnapshotPacketSize</b> etc.) will determine the live send rate.

<i>Use this to brute-force reduce the bandwidth consumption of your most impactful ghost types.</i>")]
        public byte MaxSendRate;

        /// <summary>
        /// 仅供内部使用，用于区分同一 Prefab 不同变体的 Prefab GUID
        /// </summary>
        [SerializeField]internal string prefabId = "";
        /// <summary>
        /// 添加 GhostOwner，用于跟踪哪个连接拥有此组件
        /// 必须在运行时将 GhostOwner 设置为有效的 NetworkId.Value
        /// </summary>
        [Tooltip("Automatically adds a `GhostOwner`, which allows the server to set (and track) which connection owns this ghost. In your server code, you must set the `GhostOwner` to a valid `NetworkId.Value` at runtime.")]
        public bool HasOwner;
        /// <summary>
        /// 自动向 Ghost Prefab 添加 <see cref="AutoCommandTarget"/> 组件并启用 Auto Command Target 功能
        /// 在当前连接拥有该 Ghost 且 `AutoCommandTarget.Enabled` 为 true 时，
        /// 此功能会自动向服务器发送所有 `ICommandData` 和 `IInputComponentData` Buffer
        /// </summary>
        [Tooltip("Enables the \"Auto Command Target\" feature, which automatically sends all `ICommandData` and `IInputComponentData` auto-generated buffers to the server if the following conditions are met: \n\n - The ghost is owned by the current connection (handled by user-code).\n\n - The `AutoCommandTarget` component is added to the ghost entity (enabled by this checkbox), and it's `[GhostField] public bool Enabled;` field is true (the default value).\n\nSupports both predicted and interpolated ghosts.")]
        public bool SupportAutoCommandTarget = true;
        /// <summary>
        /// 添加 CommandDataInterpolationDelay 组件，以跟踪每个客户端的插值延迟
        /// 此数据用于服务器端延迟补偿
        /// </summary>
        [Tooltip("Add a `CommandDataInterpolationDelay` component so the interpolation delay of each client is tracked.\n\nThis is used for server side lag-compensation (it allows the server to more accurately estimate how far behind your interpolated ghosts are, leading to better hit registration, for example).\n\nThis should be enabled if you expect to use input commands (from this 'Owner Predicted' ghost) to interact with other, 'Interpolated' ghosts (example: shooting or hugging another 'Player').")]
        public bool TrackInterpolationDelay;
        /// <summary>
        /// 添加 GhostGroup 组件，使此实体可以作为 Ghost Group 的根实体
        /// </summary>
        [Tooltip("Add a `GhostGroup` component, which makes it possible for this entity to be the root of a 'Ghost Group'.\n\nA 'Ghost Group' is a collection of ghosts who must always be replicated in the same snapshot, which is useful (for example) when trying to keep an item like a weapon in sync with the player carrying it.\n\nTo use this feature, you must add the target ghost entity to this `GhostGroup` buffer at runtime (e.g. when the weapon is first picked up by the player).\n\n<i>Note that GhostGroups slow down serialization, as they force entity chunk random-access. Therefore, prefer other solutions.</i>")]
        public bool GhostGroup;
        /// <summary>
        /// 强制对此 Ghost 执行一次量化，并为所有连接统一复制为 Snapshot 格式，而不是为每个连接分别执行
        /// 如果该 Ghost 几乎总会发送给至少一个连接，并且包含大量序列化组件、
        /// 子实体上的序列化组件或序列化 Buffer，则可以节省 Ghost 发送系统的 CPU 时间
        /// 角色或玩家 Ghost 是此优化的常见适用场景
        /// </summary>
        [Tooltip("CPU optimization that forces this ghost to be quantized and copied to the snapshot format <b>once for all connections</b> (instead of once <b>per connection</b>). This can save CPU time in the `GhostSendSystem` assuming all of the following:\n\n - The ghost contains many serialized components, serialized components on child entities, or serialized buffers.\n\n - The ghost is almost always sent to at least one connection.\n\n<i>Example use-cases: Players, important gameplay items like footballs and crowns, global entities like map settings and dynamic weather conditions.</i>")]
        public bool UsePreSerialization;
        /// <summary>
        /// 一项 CPU 优化，强制此 Prefab 类型使用单 Baseline 进行差分压缩
        /// 启用后会降低客户端和服务器的 CPU 开销，尤其适用于 Archetype 包含大量组件且其中多数很少变化的情况
        /// 缺点是会增加带宽，组件或 Buffer 数据变化高度可预测且呈线性时影响尤其明显，例如匀速移动或递增计数器
        /// 另一方面，当复制实体在一段时间内没有变化时，它可以避免重复发送冗余信息和 Ghost ID，从而节省部分带宽和服务器 CPU
        /// 当 Ghost 比静态优化更适合动态更新，例如无变化时刻很多但分散，或者大多数组件数据变化总体不符合线性模式时，此选项比较实用
        /// 在这些情况下，三 Baseline 的成本不足以抵消其节省的带宽
        /// </summary>
        [Tooltip("CPU optimization that forces using a single baseline for delta compression for this specific prefab type.\\nEnabling this option positively affect CPU on both client and server, especially when the archetype has a large number of components, many of which rarely change. As downside, it negatively affect the bandwidth, especially when the component/buffer data changes are highly predictable and linear (i.e moving at linear speed or incrementing a counter).\\nAs counter-balancing factor, it allow for some bandwidth saving (and CPU saving on server) when the replicated entity has no changes for a certain amount of time, avoiding re-sending \"redundant\" information and ghost ids. This becomes handy and useful in scenarios when the ghost is more suited for dynamic updates than for static optimization (i.e many no-changes moments gut sparse) and/or holistically the majority of the component data changes does not follow linear patterns, as such, the three baselines cost does not justify the saving in bandwidth.")]
        public bool UseSingleBaseline;
        /// <summary>
        /// <para>
        /// 仅用于客户端，强制此类型的<i>预测生成 Ghost</i>从客户端生成它们的 Tick 开始回滚并重新预测状态，
        /// 直到收到并分类服务器的权威生成信息
        /// 为了节省 CPU，只有收到包含此 Ghost 或其他 Ghost 新预测数据的 Snapshot 时，才会回滚 Ghost 状态
        /// </para>
        /// <para>
        /// 此选项默认为 false，表示客户端预测生成的 Ghost 在收到权威数据前不会回滚其原始状态并重新预测
        /// 这种行为通常适用于多数情况，并且 CPU 开销更低
        /// </para>
        /// </summary>
        [Tooltip("Only for client, force <i>predicted spawn ghost</i> of this type to rollback and re-predict their state from their spawn tick until the authoritative server spawn has been received and classified. In order to save some CPU, the ghost state is rollback only in case a new snapshot has been received, and it contains new predicted ghost data for this or other ghosts.\nBy default this option is set to false, meaning that predicted spawned ghost by the client never rollback their original state and re-predict until the authoritative data is received. This behaviour is usually fine in many situation and it is cheaper in term of CPU.")]
        public bool RollbackPredictedSpawnedGhostState;
        /// <summary>
        /// <para>
        /// 一项客户端 CPU 优化：发生结构变化，或通常情况下无法在预测备份中找到该实体的条目时，
        /// 强制此类型的<i>预测 Ghost</i>从最近收到的 Snapshot Tick 开始重放并重新预测状态，参见 <see cref="GhostPredictionHistorySystem"/>
        /// </para>
        /// <para>
        /// 为保留原有 1.0 行为，此选项默认为 true
        /// 启用此优化后，在客户端预测 Ghost 上移除或添加复制组件可能导致恢复值出现问题
        /// 请查阅文档，尤其是预测边界情况和已知问题部分
        /// </para>
        /// </summary>
        [Tooltip("Client CPU optimization, force <i>predicted ghost</i> of this type to replay and re-predict their state from the last received snapshot tick in case of a structural change or in general when an entry for the entity cannot be found in the prediction backup.\nBy default this option is set to true, to preserve the original 1.0 behavior. Once the optimization is turned on, removing or adding replicated components from the predicted ghost on the client may cause some issue in regard the restored value when the component is re-added. Please check the documentation for more details, in particular the <i>Prediction edge case and known issue</i> section.")]
        public bool RollbackPredictionOnStructuralChanges = true;


        /// <summary>
        /// 验证 GameObject Prefab 的名称
        /// </summary>
        /// <param name="ghostNameHash">输出根据名称生成的哈希值</param>
        /// <returns>与 gameObject.name 对应的 FixedString</returns>
        public FixedString64Bytes GetAndValidateGhostName(out ulong ghostNameHash)
        {
            var ghostName = gameObject.name;
            var ghostNameFs = new FixedString64Bytes();
            var nameCopyError = FixedStringMethods.CopyFromTruncated(ref ghostNameFs, ghostName);
            ghostNameHash = TypeHash.FNV1A64(ghostName);
            if (nameCopyError != CopyError.None)
                Debug.LogError($"{nameCopyError} when saving GhostName \"{ghostName}\" into FixedString64Bytes, became: \"{ghostNameFs}\"!", this);
            return ghostNameFs;
        }
        /// <summary>
        /// 如果可以在此 Ghost 上应用 <see cref="GhostSendType"/> 优化则为 true
        /// </summary>
        public bool SupportsSendTypeOptimization => SupportedGhostModes != GhostModeMask.All || DefaultGhostMode == GhostMode.OwnerPredicted;

        /// <summary>

        /// 辅助方法

        /// </summary>
        /// <param name="ghostName"></param>
        /// <returns></returns>
        internal GhostPrefabCreation.Config AsConfig(FixedString64Bytes ghostName)
        {
            return new GhostPrefabCreation.Config
            {
                Name = ghostName,
                Importance = Importance,
                MaxSendRate = MaxSendRate,
                SupportedGhostModes = SupportedGhostModes,
                DefaultGhostMode = DefaultGhostMode,
                // 使用 GhostGroup 时禁止 `OptimizationMode.Static`
                // 此逻辑与 GhostAuthoringComponentEditor 中的逻辑保持一致
                OptimizationMode = GhostGroup ? GhostOptimizationMode.Dynamic : OptimizationMode,
                UsePreSerialization = UsePreSerialization,
                PredictedSpawnedGhostRollbackToSpawnTick = RollbackPredictedSpawnedGhostState,
                RollbackPredictionOnStructuralChanges = RollbackPredictionOnStructuralChanges,
            };
        }
    }
}
