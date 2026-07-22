using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    ///     <para>
    /// 与 <see cref="LinkedEntityGroup"/> 类似，可以通过 <c>GhostAuthoringComponent</c>
    /// 将此 Buffer 添加到 Ghost，表示组内所有 Ghost 都应作为该 Ghost 的一部分进行序列化
    /// 注意：<c>LinkedEntityGroup</c> 会在列表中存储根实体，而 GhostGroup 不会
    ///     </para>
    ///     <para>
    /// 有关用法、细节和最佳实践，参见：https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/ghost-groups.md
    ///     </para>
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct GhostGroup : IBufferElementData
    {
        /// <summary>
        /// 子实体
        /// </summary>
        public Entity Value;
    };
}
