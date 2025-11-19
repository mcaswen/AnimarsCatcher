using Unity.Entities;

public struct FoodAmountChangedEvent : IBufferElementData
{
    public int OwnerNetworkId;
    public int Amount;
}

public struct CrystalAmountChangedEvent : IBufferElementData
{
    public int OwnerNetworkId;
    public int Amount;
}