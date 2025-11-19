using Unity.Entities;
using Unity.NetCode;
using AnimarsCatcher.Mono.Global;

public struct ResourceChangedRpc : IRpcCommand
{
    public ResourceType Type;
    public int Amount;
}
