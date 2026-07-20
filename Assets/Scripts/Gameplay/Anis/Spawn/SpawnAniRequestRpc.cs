using Unity.NetCode;
using Unity.Entities;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 客户端发送给服务器的 Ani 数量生成请求
    /// </summary>
    public struct SpawnAniRequestRpc : IRpcCommand
    {
       public int BlasterAniSpawnCount;
       public int PickerAniSpawnCount;
    }
}
