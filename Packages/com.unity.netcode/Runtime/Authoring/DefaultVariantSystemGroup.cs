using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 包含所有负责注册和设置默认 Ghost 变体的系统，参见 <see cref="GhostComponentVariationAttribute"/>
    /// 该系统组的 OnCreate 方法会收集所有已注册 <see cref="DefaultVariantSystemBase"/> 系统所使用的变体集合，
    /// 并在自身的 `OnCreate` 方法中完成默认映射
    /// 变体写入映射的顺序由创建顺序决定，参见 <see cref="CreateAfterAttribute"/> 和 <see cref="CreateBeforeAttribute"/>
    /// </summary>
    /// <remarks>
    /// 该系统组同时存在于烘焙 World 和客户端/服务器 World 中
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.BakingSystem)]
    public partial class DefaultVariantSystemGroup : ComponentSystemGroup
    {
        protected override void OnCreate()
        {
            base.OnCreate();

            // 这段逻辑放在这里可能显得突兀，但该 SystemGroup 被用作标记
            // 表示所有序列化器注册系统和默认变体注册系统都已执行完毕
            // 它必须是 SystemGroup，并且 DefaultVariants 与序列化器同时注册
            var data = SystemAPI.GetSingletonRW<GhostComponentSerializerCollectionData>().ValueRW;
            data.CollectionFinalized.Value = 1;
        }
    }
}
