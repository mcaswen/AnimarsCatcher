using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Collections;

public struct MovementOrderRpc : IRpcCommand
{
    public MovementTargetKind TargetKind;
    public float3 TargetWorldPosition;
    public Entity TargetEntity;

    public FixedList512Bytes<int> SelectedAniGhostIds;
}
