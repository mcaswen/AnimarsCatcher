using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Collections;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 客户端发送给服务器的移动目标和选中 Ani 快照
    /// </summary>
    public struct MovementOrderRpc : IRpcCommand
    {
        public MovementTargetKind TargetKind;
        public float3 TargetWorldPosition;
        public Entity TargetEntity;

        // 使用 GhostId 传递选择集，服务器再映射为权威实体
        public FixedList512Bytes<int> SelectedAniGhostIds;
    }
}
