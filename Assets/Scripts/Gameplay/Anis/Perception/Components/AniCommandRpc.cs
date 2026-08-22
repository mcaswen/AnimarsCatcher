using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 客户端发送给服务器的世界指令目标和已确认选择集版本
    /// </summary>
    public struct AniCommandRpc : IRpcCommand
    {
        public WorldCommandTargetKind TargetKind;
        public float3 TargetWorldPosition;
        public Entity TargetEntity;
        public uint SelectionVersion;
        public ulong SelectionHash;
    }
}
