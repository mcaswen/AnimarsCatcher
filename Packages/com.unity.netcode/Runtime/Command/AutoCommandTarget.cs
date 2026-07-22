using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>自动处理命令的组件，客户端负责读取和发送，服务器负责写入、使用和广播</para>
    /// <para>当 AutoCommandTarget 组件的 <see cref="Enabled"/> 为 true 时，该 Entity 会被视为客户端 <see cref="ICommandData"/> 的输入源
    /// Entity 上所有非空 Command Buffer 都会与目标 Ghost 的 ID 一同序列化到 <see cref="OutgoingCommandDataStreamBuffer"/></para>
    /// <para>在服务器端，从 <see cref="IncomingCommandDataStreamBuffer"/> 反序列化命令后，
    /// 系统会查找对应 Entity；如果其 AutoCommandTarget 组件已启用，就把命令添加到相应的输入 Command Buffer</para>
    /// </summary>
    /// <remarks>
    /// 使用 AutoCommandTarget 时，目标 Entity 必须具有 <see cref="GhostOwner"/>
    /// </remarks>
    [DontSupportPrefabOverrides]
    [GhostComponent(SendDataForChildEntity = true)]
    public struct AutoCommandTarget : IComponentData
    {
        /// <summary>
        /// 启用或禁用当前 Entity 的命令发送与接收
        /// 可以同时启用多个 Entity
        /// </summary>
        [GhostField] public bool Enabled;
    }
}
