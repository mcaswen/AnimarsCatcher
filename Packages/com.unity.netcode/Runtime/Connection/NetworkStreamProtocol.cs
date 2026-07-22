namespace Unity.NetCode
{
    /// <summary>
    /// NetCode 发送的消息类型
    /// </summary>
    public enum NetworkStreamProtocol
    {
        /// <summary>
        /// 包含输入命令的数据包，始终从客户端发送到服务器
        /// </summary>
        Command,
        /// <summary>
        /// 模拟 Snapshot，从服务器发送到客户端
        /// </summary>
        Snapshot,
        /// <summary>
        /// 包含单个 RPC 的消息，客户端和服务器都可以发送
        /// </summary>
        Rpc
    }
}
