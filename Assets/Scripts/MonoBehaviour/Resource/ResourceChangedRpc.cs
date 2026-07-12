using Unity.Entities;
using Unity.NetCode;
using AnimarsCatcher.Mono.Global;

/// <summary>
/// 客户端提交给服务端的资源变化请求
/// </summary>
public struct ResourceChangedRpc : IRpcCommand
{
    public ResourceType Type;
    public int Amount;
}
