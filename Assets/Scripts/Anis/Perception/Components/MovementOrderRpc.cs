using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

public struct MovementOrderRpc : IRpcCommand
{
    public MovementTargetKind TargetKind;
    public float3 TargetWorldPosition;
    public Entity TargetEntity;
}
