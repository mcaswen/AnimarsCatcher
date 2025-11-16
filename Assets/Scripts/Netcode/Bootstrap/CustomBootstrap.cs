using Unity.NetCode;
using Unity.Networking.Transport;

public class CustomBootstrap : ClientServerBootstrap
{
    public override bool Initialize(string defaultWorldName)
    {
#if UNITY_EDITOR
        // 编辑器创建
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
        // 非 Editor 模式下，角色由 NetRuntimeRole 决定
        DefaultConnectAddress = NetworkEndpoint.LoopbackIpv4;
        AutoConnectPort = 0;

        switch (NetRuntimeRole.Current)
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
#endif
    }
}
