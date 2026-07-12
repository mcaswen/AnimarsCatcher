using Unity.Mathematics;

/// <summary>
/// 保存一次输入转换所需的时间和网络 Tick
/// </summary>
public readonly struct InputContext
{
    public readonly float DeltaTime;
    public readonly uint NetworkTick;
    public readonly float RightLongPressThreshold;

    /// <summary>
    /// 创建本帧输入转换上下文
    /// </summary>
    /// <param name="deltaTime">本帧时长</param>
    /// <param name="tick">当前网络 Tick</param>
    /// <param name="longPressThreshold">右键长按阈值</param>
    public InputContext(float deltaTime, uint tick, float longPressThreshold)
    {
        DeltaTime = deltaTime; NetworkTick = tick; RightLongPressThreshold = longPressThreshold;
    }
}

/// <summary>
/// 保存从 Input System 读取的原始键鼠状态
/// </summary>
public struct KeyboardMouseState
{
    public float2 Move;
    public float2 LookDelta;
    public float Scroll;
    public bool SpaceDown;
    public bool EKeyDown;
    public bool EscapeKeyDown;
    public bool LeftMousePressed;
    public bool RightHeld;
    public float2 MousePosition;
}

/// <summary>
/// 把原始键鼠状态转换为可预测的玩家输入组件
/// </summary>
public static class PlayerInputFeature
{
    /// <summary>
    /// 更新相机、鼠标按钮和长按脉冲
    /// </summary>
    /// <param name="input">待更新玩家输入</param>
    /// <param name="rawInputState">原始键鼠状态</param>
    /// <param name="context">输入转换上下文</param>
    public static void ApplyMouseInputs(ref PlayerInput input, in KeyboardMouseState rawInputState, in InputContext context)
    {
        input.CameraLookInput = rawInputState.LookDelta;
        input.CameraZoomInput = rawInputState.Scroll;

        if (rawInputState.RightHeld)
        {
            SetRightMouseHeldTimeAndLongPress(ref input, in context);
        }
        else
        {
            input.RightMouseHeld = 0;
            input.RightMouseHeldTime = 0f;
        }

        input.MousePosition = rawInputState.MousePosition;
        if (rawInputState.LeftMousePressed)
            input.LeftMousePressed.Set(context.NetworkTick);
    }

    private static void SetRightMouseHeldTimeAndLongPress(ref PlayerInput input, in InputContext context)
    {
        input.RightMouseHeld = 1;

        float previousHeldTime = input.RightMouseHeldTime;
        input.RightMouseHeldTime = previousHeldTime + context.DeltaTime;

        // 只在跨过阈值的 Tick 产生一次长按脉冲
        if (previousHeldTime < context.RightLongPressThreshold && input.RightMouseHeldTime >= context.RightLongPressThreshold)
            input.RightMouseLongPress.Set(context.NetworkTick);
    }

    
    /// <summary>
    /// 更新移动输入和键盘按键脉冲
    /// </summary>
    /// <param name="input">待更新玩家输入</param>
    /// <param name="rawInputState">原始键鼠状态</param>
    /// <param name="context">输入转换上下文</param>
    public static void ApplyKeyboardInput(ref PlayerInput input, in KeyboardMouseState rawInputState, in InputContext context)
    {
        input.MoveInput = rawInputState.Move;

        if (rawInputState.SpaceDown) input.JumpPressed.Set(context.NetworkTick);
        if (rawInputState.EKeyDown) input.InteractPressed.Set(context.NetworkTick);
        if (rawInputState.EscapeKeyDown) input.PausePressed.Set(context.NetworkTick);
    }

}
