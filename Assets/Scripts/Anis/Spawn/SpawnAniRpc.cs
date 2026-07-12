using Unity.NetCode;
using Unity.Entities;

public struct SpawnAniRpc : IRpcCommand
{
   public int BlasterAniSpawnCount;
   public int PickerAniSpawnCount;
}