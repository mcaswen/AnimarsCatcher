using Unity.Collections;
using Unity.Entities;

public struct ServerMatchStartState : IComponentData
{
    public FixedString64Bytes SceneName;
    public byte MatchStartRequested;   
    public byte ClientStartRpcSent;   
    public byte CharactersSpawned;    
}