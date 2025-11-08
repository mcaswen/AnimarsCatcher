using Unity.Mathematics;
using Unity.NetCode;
using Unity.Collections;
using Unity.Entities;
using System;

[Flags]
public enum CommandButtons : byte
{
    RMBHold = 1 << 0,
    RMBLong = 1 << 1, // 过阈值当帧的脉冲
    Jump = 1 << 2,
    Interact = 1 << 3,
    Pause = 1 << 4
}

[InternalBufferCapacity(16)]
[GhostComponent]
public struct InputCommand : ICommandData
{
    [GhostField]
    public NetworkTick Tick { get; set; }

    [GhostField]
    public float3 Move;

    [GhostField]
    public float2 Look;

    [GhostField]
    public float2 Zoom;

    [GhostField]
    public CommandButtons Buttons;
}
