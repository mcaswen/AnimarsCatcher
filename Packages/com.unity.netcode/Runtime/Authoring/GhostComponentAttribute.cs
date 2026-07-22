using System;

namespace Unity.NetCode
{
    /// <summary>
    /// 此特性用于标记组件，以控制它们包含在哪些 Ghost Prefab 变体中，以及针对所有者预测 Ghost 发送到哪里
    /// </summary>
    /// <remarks>
    /// 仅使用 GhostComponent 不足以复制组件，请确保在每个需要复制的字段上使用 <see cref="GhostFieldAttribute"/>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class|AttributeTargets.Struct)]
    public class GhostComponentAttribute : Attribute
    {
        /// <summary>
        /// 获取或设置应在何种 Prefab 类型的主实体上包含此组件
        /// </summary>
        public GhostPrefabType PrefabType { get; set; } = GhostPrefabType.All;
        /// <summary>
        /// 获取或设置当 Ghost 使用所有者预测时，应向哪种 Ghost 发送此组件
        /// 旧名称为 OwnerPredictedSendType
        /// </summary>
        public GhostSendType SendTypeOptimization { get; set; } = GhostSendType.AllClients;

        /// <summary>
        /// 获取或设置是否应向预测所有者发送组件
        /// 某些参数与 OwnerSendType 的组合可能在代码生成阶段产生错误或警告
        /// </summary>
        public SendToOwnerType OwnerSendType { get; set; } = SendToOwnerType.All;

        /// <summary>
        /// 表示将此组件添加到子实体时，是否应发送（即复制）其数据
        /// NetCode 默认不会复制子实体上的组件和 Buffer 数据
        /// 这是因为该过程需要在其他 Chunk 中查找子实体，开销较高
        /// 因此，将此标志设为 true 会启用开销更高的子实体序列化，除非被其他变体覆盖
        /// 设为 false 不会产生影响，这也是默认行为
        /// </summary>
        public bool SendDataForChildEntity { get; set; } = false;
    }
}
