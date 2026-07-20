using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay.Contracts
{
    /// <summary>
    /// 客户端提交给服务端的资源变化请求
    /// </summary>
    public struct DebugAdjustResourceRpc : IRpcCommand
    {
        public ResourceItemKind Kind;
        public int Amount;
    }
}
