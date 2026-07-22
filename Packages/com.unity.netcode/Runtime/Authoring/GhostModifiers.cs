// 重要提示：此文件由 NetCode 源码生成器共享
// 此处不允许引用 UnityEngine、UnityEditor 或其他包的 DLL
// 如果修改此文件，请记得重新编译源码生成器

using System;

namespace Unity.NetCode
{
    /// <summary>
    /// 分配给每个 <see cref="GhostInstance"/>，表示允许此组件存在于哪些 Ghost Prefab 版本上
    /// 可使用此枚举禁用 Ghost 服务器版本上的渲染组件
    /// 如果无法更改 ComponentType，请使用 `GhostAuthoringInspectionComponent` 在指定 Ghost Prefab 上手动覆盖
    /// </summary>
    [Flags]
    public enum GhostPrefabType
    {
        /// <summary>
        /// 不会把组件添加到任何 Ghost Prefab 类型
        /// </summary>
        None = 0,
        /// <summary>
        /// 只会把组件添加到 <see cref="GhostMode.Interpolated"/> 客户端版本
        /// </summary>
        InterpolatedClient = 1,
        /// <summary>
        /// 只会把组件添加到 <see cref="GhostMode.Predicted"/> 客户端版本
        /// </summary>
        PredictedClient = 2,
        /// <summary>
        /// 只会把组件添加到客户端版本
        /// </summary>
        Client = 3,
        /// <summary>
        /// 只会把组件添加到服务器版本
        /// </summary>
        Server = 4,
        /// <summary>
        /// 只会把组件添加到服务器和 PredictedClient 版本
        /// </summary>
        AllPredicted = 6,
        /// <summary>
        /// 会把组件添加到所有版本
        /// </summary>
        All = 7
    }

    /// <summary>
    /// <para>一种优化方式：通过 <see cref="GhostComponentAttribute"/> 或变体在每个 GhostComponent 上设置</para>
    /// <para>当 Ghost 使用 <see cref="GhostMode.OwnerPredicted"/>，或者其 SupportedGhostModes 在编译阶段已知时，
    /// 此标志会筛选哪些类型的客户端能够接收数据更新</para>
    /// <para>映射到每个 Ghost 的 <see cref="GhostMode"/></para>
    /// <para>请注意，如果 Ghost 的 <see cref="GhostMode"/> 可以在运行时修改，则<b>无法</b>使用此优化</para>
    /// </summary>
    /// <remarks>
    /// <para>GhostSendType 适用于 OwnerPredicted Ghost，原因如下：</para>
    /// <para>- 服务器<b>可以</b>推断指定客户端上的 OwnerPredicted Ghost 会采用哪种 GhostMode
    /// 判断方式很简单：如果客户端是所有者则使用预测，否则使用插值</para>
    /// <para>- 对于同时支持 Predicted 和 Interpolated 的 Ghost，服务器<b>无法</b>推断其当前 GhostMode，
    /// 因为该模式可以在运行时改变，参见 <see cref="GhostPredictionSwitchingQueues"/>
    /// 因此，两种模式必须采用相同的服务器 Snapshot 序列化策略</para>
    /// <para>GhostSendType <i>也</i>适用于未使用 <see cref="GhostModeMask.All"/> 的 Ghost，原因如下：</para>
    /// <para>- 由于 GhostMode 无法在运行时改变，服务器<b>可以</b>推断指定客户端上的 Ghost 会采用哪种模式</para>
    /// <para>适用于父实体和子实体上的所有组件</para>
    /// </remarks>
    /// <example>
    /// 只有在客户端预测 Ghost 时，才可能需要速度组件，以便正确预测速度和碰撞
    /// 因此，应在 Velocity 组件上使用 GhostSendType.Predicted
    /// </example>
    [Flags]
    public enum GhostSendType
    {
        /// <summary>服务器永远不会向任何客户端复制此组件
        /// 行为与 <see cref="DontSerializeVariant"/> 类似，因此在使用 DontSerializeVariant 时属于冗余设置</summary>
        DontSend = 0,
        /// <summary>
        /// 服务器只会向正在插值此 Ghost 的客户端复制该组件，参见 <see cref="GhostMode.Interpolated"/>
        /// </summary>
        OnlyInterpolatedClients = 1,
        /// <summary>
        /// 服务器只会向正在预测此 Ghost 的客户端复制该组件，参见 <see cref="GhostMode.Predicted"/>
        /// </summary>
        OnlyPredictedClients = 2,
        /// <summary>
        /// 服务器始终复制此组件，也是默认设置
        /// </summary>
        AllClients = 3
    }

    /// <summary>
    /// <see cref="GhostComponentAttribute"/> 的元数据，表示服务器是否应将 GhostField 值复制回客户端
    /// </summary>
    /// <remarks>
    /// 通常由 <see cref="IInputComponentData"/> 结构体使用，只把每个客户端的输入复制给其他玩家
    /// </remarks>
    [Flags]
    public enum SendToOwnerType
    {
        /// <summary>
        /// 指示服务器不要向任何客户端复制此组件
        /// </summary>
        None = 0,
        /// <summary>
        /// 指示服务器只向所有者复制此组件
        /// </summary>
        SendToOwner = 1,
        /// <summary>指示服务器向除 Ghost 所有者之外的所有客户端复制此组件
        /// Ghost 所有者即拥有该 Ghost 的玩家</summary>
        SendToNonOwner = 2,
        /// <summary>
        /// 指示服务器向包括 Ghost 所有者在内的所有客户端复制此组件
        /// </summary>
        All = 3,
    }

    /// <summary>

    /// 表示从 Snapshot 接收 <see cref="GhostFieldAttribute"/> 值时采用的反序列化方式

    /// </summary>
    public enum SmoothingAction
    {
        /// <summary>
        /// GhostField 值会在最新 Snapshot 值可用时钳制到该值
        /// </summary>
        Clamp = 0,

        /// <summary>在最近两个已处理 Snapshot 值之间插值 GhostField 值，如果下一 Tick 没有可用数据，则钳制到最新 Snapshot 值
        /// 如果抖动过大或延迟过高，请调整 <see cref="ClientTickRate"/> 的插值参数</summary>
        Interpolate = 1 << 0,

        /// <summary>
        /// 在 Snapshot 值之间插值 GhostField 值，如果下一 Tick 没有可用数据，则使用前两个 Snapshot 值线性外推下一个值
        /// 外推范围通过 <see cref="ClientTickRate.MaxExtrapolationTimeSimTicks"/> 进行限制，即钳制
        /// </summary>
        /// <remarks>
        /// 请注意，使用静态优化的插值 Ghost <b>永远不会</b>执行外推
        /// 这是因为它们不会发送零变化 Snapshot 更新，即 GhostField 差分压缩结果全部为零的 Snapshot 更新，
        /// 因而无法区分“这个持续变化的值已经停止变化”和“尚未收到下一个连续值”
        /// </remarks>
        InterpolateAndExtrapolate = 3,
    }
}
