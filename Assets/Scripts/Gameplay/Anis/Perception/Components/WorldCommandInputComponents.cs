using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 表示一次点击命中的交互目标类别
    /// </summary>
    public enum WorldCommandTargetKind : byte
    {
        None = 0,
        Ground = 1,
        Player = 2,
        Ani = 3,
        // 可拾取资源
        Resource = 4,
        // 可攻击基地
        Base = 5,
    }

    /// <summary>
    /// 保存客户端尚待射线解析的屏幕点击输入
    /// </summary>
    public struct WorldCommandClickRequest : IComponentData
    {
        public int Version;
        public float2 ScreenPosition;
    }

    /// <summary>
    /// 保存最近一次点击射线解析出的目标和世界坐标
    /// </summary>
    public struct WorldCommandRaycastResult : IComponentData
    {
        public int Version;
        public WorldCommandTargetKind TargetKind;
        public Entity TargetEntity;
        public float3 TargetWorldPosition;
    }

    /// <summary>
    /// 记录已经发送过 RPC 的点击结果版本，防止重复下令
    /// </summary>
    public struct WorldCommandSentVersion : IComponentData
    {
        public int Version;
    }
}
