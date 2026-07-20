using Unity.Entities;

namespace AnimarsCatcher.Gameplay.Contracts
{
    /// <summary>
    /// 表示等待服务器在本帧汇总处理的一次伤害
    /// </summary>
    public struct DamageEvent : IBufferElementData
    {
        public int Amount;
    }
}
