using System;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于升级到新组件类型的临时类型，将在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("GhostOwnerComponent has been deprecated. Use GhostOwner instead (UnityUpgradable) -> GhostOwner", true)]
    [DontSupportPrefabOverrides]
    public struct GhostOwnerComponent : IComponentData
    {}

    /// <summary>
    /// <para>
    /// GhostOwner 是可添加到 Ghost 的可选组件，用于在实体和特定客户端之间建立关联
    /// 例如生成该实体的客户端、子弹所属客户端或玩家实体所属客户端
    /// 它通常添加到预测 Ghost，参见 <see cref="PredictedGhost"/>，但也可以存在于插值 Ghost 上
    /// </para>
    /// <para>
    /// 以下情况必须添加 <see cref="GhostOwner"/>
    /// </para>
    /// <para>- Ghost 配置为所有者预测 <see cref="GhostMode"/> 时，因为必须区分由谁预测，即所有者，以及由谁插值该 Ghost
    /// </para>
    /// <para>- 需要启用远程玩家预测时，参见 <see cref="ICommandData"/>
    /// 或一般情况下需要基于所有权发送数据时，参见 <see cref="SendToOwnerType.SendToOwner"/>
    /// </para>
    /// <para>- 需要使用 <see cref="AutoCommandTarget"/> 功能时</para>
    /// </summary>
    [DontSupportPrefabOverrides]
    [GhostComponent(SendDataForChildEntity = true)]
    public struct GhostOwner : IComponentData
    {
        /// <summary>
        /// 与此实体关联的客户端 <see cref="NetworkId"/>
        /// </summary>
        [GhostField] public int NetworkId;
    }

    /// <summary>
    /// 表示当前 World 拥有某个 Ghost 输入所有权的可启用组件
    /// 例如玩家 Ghost 仅在拥有它的客户端上启用此组件
    /// 当 Ghost 的 <see cref="GhostOwner.NetworkId"/> 与客户端上的 <see cref="NetworkId.Value"/> 匹配时启用
    /// 对于 <see cref="NetCodeConfig.HostWorldMode.SingleWorld"/>，它匹配带有 <see cref="LocalConnection"/> 标签的连接
    /// 对于独立二进制中的服务器 World，此值未定义
    /// 不应在预测组内使用此组件，如需在预测组内区分 Ghost
    /// 请使用 <see cref="GhostComponentAttribute"/>，使 Command 和输入仅保留在预测 Ghost 上
    /// </summary>
    public struct GhostOwnerIsLocal : IComponentData, IEnableableComponent
    {}
}
