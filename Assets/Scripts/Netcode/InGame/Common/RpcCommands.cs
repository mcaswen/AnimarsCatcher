using Unity.NetCode;
using Unity.Entities;
using Unity.Collections;

public struct GoInGameRequest : IRpcCommand {}

public struct ClientLobbyIntroRpc : IRpcCommand
{
    public FixedString64Bytes PlayerName;
}

public struct StartGameRpc : IRpcCommand
{
    public FixedString64Bytes SceneName;
}

public struct ClientStartGameRpc : IRpcCommand
{
    public FixedString64Bytes SceneName;
}

public struct SetInGameRpc : IRpcCommand
{
}

public struct ClientMatchStartState : IComponentData
{
    public byte Active;
}
