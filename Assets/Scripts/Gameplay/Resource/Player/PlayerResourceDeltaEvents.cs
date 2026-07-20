using Unity.Entities;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 指定玩家食物数量的待应用变化
    /// </summary>
    public struct FoodResourceDeltaEvent : IBufferElementData
    {
        public int OwnerNetworkId;
        public int Amount;
    }

    /// <summary>
    /// 指定玩家水晶数量的待应用变化
    /// </summary>
    public struct CrystalResourceDeltaEvent : IBufferElementData
    {
        public int OwnerNetworkId;
        public int Amount;
    }
}
