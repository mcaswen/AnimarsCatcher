using Unity.NetCode;
using Unity.Networking.Transport;

public class CustomBootstrap : ClientServerBootstrap
{
    public override bool Initialize(string defaultWorldName)
    {
#if UNITY_EDITOR
        // 编辑器创建
        DefaultConnectAddress = NetworkEndpoint.LoopbackIpv4;
        AutoConnectPort = 7979;
        
        switch (RequestedPlayType)
        {
            case PlayType.ClientAndServer:
                CreateServerWorld(defaultWorldName);
                CreateClientWorld(defaultWorldName);

                for (int i = 0; i < RequestedNumThinClients; i++)
                    CreateThinClientWorld();
                return true;

            case PlayType.Client:
                CreateClientWorld(defaultWorldName);

                for (int i = 0; i < RequestedNumThinClients; i++)
                    CreateThinClientWorld();
                return true;

            case PlayType.Server:
                CreateServerWorld(defaultWorldName);
                return true;
        }
        return true;
#else
        // 命令行创建
        DefaultConnectAddress = NetworkEndpoint.LoopbackIpv4;
        AutoConnectPort    = 0; 
        
        if (CommandLineManager.HasArg("-dedicated") || CommandLineManager.HasArg("-server") || CommandLineManager.HasArg("-serverui"))
            CreateServerWorld(defaultWorldName);
        else
            CreateClientWorld(defaultWorldName);
        return true;
#endif
    }
}
