namespace Unity.NetCode
{
    /// <summary>
    /// <para>一种特殊的通用组件变体，配置 GhostComponentSerializerCollectionSystemGroup 时可分配给任意组件或 Buffer
    /// 主要用于从服务器端 Ghost Prefab 中裁剪组件</para>
    /// <para>如需用于自己的类型，请在自己的 <see cref="DefaultVariantSystemBase.RegisterDefaultVariants"/> 方法中将其设为默认变体</para>
    /// </summary>
    public sealed class ClientOnlyVariant
    {
    }
    /// <summary>
    /// <para>一种特殊的通用组件变体，配置 GhostComponentSerializerCollectionSystemGroup 时可分配给任意组件或 Buffer
    /// 主要用于从客户端 Ghost Prefab 中裁剪组件</para>
    /// <para>如需用于自己的类型，请在自己的 <see cref="DefaultVariantSystemBase.RegisterDefaultVariants"/> 方法中将其设为默认变体</para>
    /// </summary>
    public sealed class ServerOnlyVariant
    {
    }

    /// <summary>
    /// 一种可以分配给任意组件或 Buffer 的特殊通用组件变体
    /// 当组件序列化器设为 DontSerializeVariant 时，不会从 Prefab 的客户端或服务器版本中裁剪组件本身，
    /// 但运行时<b>不会</b>序列化该组件，因此也<b>不会</b>发送给客户端
    /// </summary>
    /// <remarks>`DontSerializeVariant` 是所有子实体的默认变体，并自动对所有序列化类型可用</remarks>
    public sealed class DontSerializeVariant
    {
    }
}
