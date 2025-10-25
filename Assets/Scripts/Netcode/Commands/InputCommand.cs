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
public struct InputCommand : ICommandData
{
    public NetworkTick Tick { get; set; }
    public float3 Move;
    public float2 Look;
    public float2 Zoom;
    public CommandButtons Buttons;

    public uint RMBHoldStartTick;
    public ushort RMBHeldTicks; 
    
    public float2 MousePosition; 
    
    // public Entity ControlledEntity; // 方便调试查看命令对应的实体
}
