using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// 保存客户端采集并按网络 Tick 消费的玩家输入状态
/// </summary>
public struct PlayerInput : IComponentData
{
    // 连续移动和相机输入
    public float2 MoveInput;
    public float2 CameraLookInput;
    public float CameraZoomInput;
    
    // 键盘脉冲输入
    public FixedInputEvent JumpPressed;
    public FixedInputEvent InteractPressed;
    public FixedInputEvent PausePressed;

    // 鼠标状态和脉冲输入
    public FixedInputEvent LeftMousePressed;
    public byte RightMouseHeld;
    public float RightMouseHeldTime;
    public FixedInputEvent RightMouseLongPress;
    public float2 MousePosition;
}

/// <summary>
/// 记录当前请求屏蔽玩法输入的 UI 面板数量
/// </summary>
public struct PlayerInputLockState : IComponentData
{
    // 锁计数 > 0 表示当前有面板占用输入
    public int LockCount;
}
