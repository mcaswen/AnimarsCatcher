/// <summary>定义支持的服务器启动参数类型</summary>
public enum ServerLaunchArgumentType { DedicatedServer, ServerUI }

/// <summary>集中定义 NetCode 使用的端口</summary>
public static class NetworkPorts
{
    public const ushort Game = 7979;
}

/// <summary>集中定义服务器进程启动参数</summary>
public static class ServerLaunchArguments
{
    public const string Dedicated = "-dedicated";
    public const string ServerUI = "-serverui";

    /// <summary>将启动参数类型转换为命令行文本</summary>
    /// <param name="argumentType">启动参数类型</param>
    /// <returns>命令行参数文本</returns>
    public static string GetCommandLineArgument(ServerLaunchArgumentType argumentType)
        => argumentType == ServerLaunchArgumentType.DedicatedServer ? Dedicated : ServerUI;
}

/// <summary>集中定义 NetCode World 名称</summary>
public static class NetworkWorldNames
{
    public const string Default = "Default";
}
