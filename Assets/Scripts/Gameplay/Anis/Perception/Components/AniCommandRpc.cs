using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Collections;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 客户端发送给服务器的世界指令目标和选中 Ani 快照
    /// </summary>
    public struct AniCommandRpc : IRpcCommand
    {
        public WorldCommandTargetKind TargetKind;
        public float3 TargetWorldPosition;
        public Entity TargetEntity;

        // 使用 GhostId 传递选择集，服务器收到后再映射到自己的 Entity
        public FixedList512Bytes<int> SelectedAniGhostIds;
    }
}
