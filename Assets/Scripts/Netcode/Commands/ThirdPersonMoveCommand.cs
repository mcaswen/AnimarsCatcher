using Unity.Mathematics;
using Unity.NetCode;
using Unity.Collections;
using Unity.Entities;

[InternalBufferCapacity(256)]
public struct ThirdPersonMoveCommand : ICommandData
{
    public NetworkTick Tick { get; set; }
    public float3 Move;
    public float2 Look;
    public float2 Zoom;
    public bool Jump;
    public Entity ControlledEntity; // 方便调试查看命令对应的实体
}
