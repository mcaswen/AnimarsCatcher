namespace AnimarsCatcher.Player
{
    using Unity.Mathematics;
    using Unity.NetCode;
    using Unity.Collections;
    using Unity.Entities;
    using System;

    /// <summary>
    /// 定义随网络输入命令发送的离散按键位
    /// </summary>
    [Flags]
    public enum CommandButtons : byte
    {
        RMBHold = 1 << 0,
        // 仅在越过长按阈值的 Tick 置位
        RMBLong = 1 << 1,
        Jump = 1 << 2,
        Interact = 1 << 3,
        Pause = 1 << 4
    }

    /// <summary>
    /// 保存一个网络 Tick 内需要预测和回滚的玩家命令
    /// </summary>
    [InternalBufferCapacity(16)]
    [GhostComponent]
    public struct InputCommand : ICommandData
    {
        // NetCode 用于匹配预测和回滚的服务器 Tick
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
}
