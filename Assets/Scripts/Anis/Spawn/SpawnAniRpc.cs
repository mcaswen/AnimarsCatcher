using Unity.NetCode;
using Unity.Entities;

/// <summary>
/// 客户端发送给服务器的 Ani 数量生成请求
/// </summary>
public struct SpawnAniRpc : IRpcCommand
{
   public int BlasterAniSpawnCount;
   public int PickerAniSpawnCount;
}
