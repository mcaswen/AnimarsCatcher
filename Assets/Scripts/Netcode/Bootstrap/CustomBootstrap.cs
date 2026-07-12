using Unity.NetCode;
using Unity.Networking.Transport;
using AnimarsCatcher.Mono.Global;

/// <summary>
/// 根据编辑器播放模式或运行时网络角色创建 NetCode World
/// </summary>
public class CustomBootstrap : ClientServerBootstrap
{
    /// <summary>
    /// 创建当前进程需要的客户端、服务器和 Thin Client World
    /// </summary>
    /// <param name="defaultWorldName">Unity 提供的默认 World 名称</param>
    /// <returns>是否已完成自定义 World 初始化</returns>
    public override bool Initialize(string defaultWorldName)
    {
#if UNITY_EDITOR
        // 编辑器使用 Multiplayer PlayMode 请求决定 World 组合
        DefaultConnectAddress = NetworkEndpoint.LoopbackIpv4;
        AutoConnectPort = 0;
        
        switch (RequestedPlayType)
        {
            case PlayType.ClientAndServer:
                CreateServerWorld("Server World");
                CreateClientWorld("Client World");

                for (int i = 0; i < RequestedNumThinClients; i++)
                    CreateThinClientWorld();
                return true;

            case PlayType.Client:
                CreateClientWorld("Client World");

                for (int i = 0; i < RequestedNumThinClients; i++)
                    CreateThinClientWorld();
                return true;

            case PlayType.Server:
                CreateServerWorld("Server World");
                return true;
        }
        return true;
#else
        // Player 构建由启动流程写入的 NetworkRuntimeRole 决定进程职责
        DefaultConnectAddress = NetworkEndpoint.LoopbackIpv4;
        AutoConnectPort = 0;

        switch (NetworkRuntimeRole.Current)
        {
            case NetworkRunRole.Host:
                CreateServerWorld("Server World");
                CreateClientWorld("Client World");
                return true;

            case NetworkRunRole.Client:
                CreateClientWorld("Client World");
                return true;

            case NetworkRunRole.DedicatedServer:
                CreateServerWorld("Server World");
                return true;
        }

        return false;
#endif
    }
}
