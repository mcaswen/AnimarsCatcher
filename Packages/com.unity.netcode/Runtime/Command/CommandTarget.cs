using System;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于升级到新组件类型的临时类型，将在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("CommandTargetComponent has been deprecated. Use CommandTarget instead (UnityUpgradable) -> CommandTarget", true)]
    public struct CommandTargetComponent : IComponentData
    {}

    /// <summary>
    /// <para>添加到所有 <see cref="NetworkStreamConnection"/> 的组件，保存命令读取目标（客户端）
    /// 或命令写入目标（服务器）的 Entity 引用
    /// 在以下情况下，必须为 <see cref="targetEntity"/> 设置有效引用才能接收客户端命令：</para>
    /// <para><list type="bullet">
    /// <item>没有使用 AutoCommandTarget</item>
    /// <item>需要支持 Thin Client，因为 AutoCommandTarget 在这种情况下不起作用
    /// AutoCommandTarget 和 CommandTarget 可以互补使用，也可以同时使用</item></list></para>
    /// </summary>
    /// <remarks>
    /// 目标 Entity 必须至少具有一个 `ICommandData` 组件
    /// </remarks>
    public struct CommandTarget : IComponentData
    {
        /// <inheritdoc cref="CommandTarget"/>
        public Entity targetEntity;
    }
}
