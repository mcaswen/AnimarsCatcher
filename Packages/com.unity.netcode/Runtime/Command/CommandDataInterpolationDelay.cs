using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>用于访问插值延迟的可选组件，以便在服务器上实现延迟补偿
    /// 该组件也存在于预测客户端上，但插值延迟始终为 0</para>
    /// <para>默认情况下转换过程不会烘焙此组件，用户应在以下两个时机之一显式添加：</para>
    /// <para> 1. 转换时：使用 `GhostAuthoringComponent` 中的复选框或自定义 Baker</para>
    /// <para> 2. 运行时：Entity 生成后</para>
    /// </summary>
    /// <remarks>
    /// 当 Ghost 具有此组件时，<see cref="CommandReceiveSystem{TCommandDataSerializer,TCommandData}.ReceiveJobData"/>
    /// 会使用该连接最近上报的插值延迟自动更新 <see cref="Delay"/>，
    /// 这里的连接是为此 Entity 发送命令的连接
    /// 因此，只有被预测且至少具有一个输入 Command Buffer 的 Entity 才会更新此组件
    /// </remarks>
    [GhostComponent(PrefabType = GhostPrefabType.AllPredicted)]
    public struct CommandDataInterpolationDelay : IComponentData
    {
        /// <summary>
        /// 此 Entity 最近一次上报的插值延迟，单位为 Tick
        /// 由于 Command Header 包含插值延迟，目标 Entity 每次收到客户端命令时都会更新该值
        /// 如果客户端通过修改 <see cref="CommandTarget"/> 或启用另一个 <see cref="AutoCommandTarget"/>
        /// 来切换命令目标，例如进入载具，该延迟值就会过期
        /// 换言之，该值不会重置为 0，而会保留最后一条已接收命令上报的值
        /// </summary>
        public uint Delay;
    }
}
