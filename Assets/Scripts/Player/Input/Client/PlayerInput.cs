using Unity.Entities;
using Unity.Mathematics;

public struct PlayerInput : IComponentData
{
    // 移动/相机
    public float2 MoveInput;
    public float2 CameraLookInput;
    public float CameraZoomInput;
    
    // 键盘
    public FixedInputEvent JumpPressed;
    public FixedInputEvent InteractPressed;
    public FixedInputEvent PausePressed;

    // 鼠标
    public FixedInputEvent LeftMousePressed;
    public byte RightMouseHeld;
    public float RightMouseHeldTime;
    public FixedInputEvent RightMouseLongPress;
    public float2 MousePosition;
}