using Unity.Mathematics;
using Unity.NetCode;
using Unity.Collections;
using Unity.Entities;
using System;

/// <summary>定义随网络输入命令发送的离散按键位</summary>
[Flags]
public enum CommandButtons : byte
{
    RMBHold = 1 << 0,
    RMBLong = 1 << 1, // 仅在越过长按阈值的 Tick 置位
    Jump = 1 << 2,
    Interact = 1 << 3,
    Pause = 1 << 4
}

/// <summary>保存一个网络 Tick 内需要预测和回滚的玩家命令</summary>
[InternalBufferCapacity(16)]
[GhostComponent]
public struct InputCommand : ICommandData
{
    /// <summary>命令所属的服务器 Tick</summary>
    [GhostField]
    public NetworkTick Tick { get; set; }

    /// <summary>服务器和客户端共同使用的世界空间移动向量</summary>
    [GhostField]
    public float3 Move;

    /// <summary>相机视角输入增量</summary>
    [GhostField]
    public float2 Look;

    /// <summary>相机缩放输入增量</summary>
    [GhostField]
    public float2 Zoom;

    /// <summary>同一 Tick 内触发的离散按键位</summary>
    [GhostField]
    public CommandButtons Buttons;
}
