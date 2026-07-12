using Unity.Entities;

/// <summary>
/// 指定玩家食物数量的待应用变化
/// </summary>
public struct FoodAmountChangedEvent : IBufferElementData
{
    public int OwnerNetworkId;
    public int Amount;
}

/// <summary>
/// 指定玩家水晶数量的待应用变化
/// </summary>
public struct CrystalAmountChangedEvent : IBufferElementData
{
    public int OwnerNetworkId;
    public int Amount;
}
