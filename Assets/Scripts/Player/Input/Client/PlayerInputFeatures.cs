using Unity.Mathematics;

/// <summary>保存一次输入转换所需的时间和网络 Tick</summary>
public readonly struct InputContext
{
    public readonly float DeltaTime;
    public readonly uint NetTick;
    public readonly float RightLongPressThreshold;

    /// <summary>创建本帧输入转换上下文</summary>
    /// <param name="deltaTime">本帧时长</param>
    /// <param name="tick">当前网络 Tick</param>
    /// <param name="longPressThreshold">右键长按阈值</param>
    public InputContext(float deltaTime, uint tick, float longPressThreshold)
    {
        DeltaTime = deltaTime; NetTick = tick; RightLongPressThreshold = longPressThreshold;
    }
}

/// <summary>保存从 Input System 读取的原始键鼠状态</summary>
public struct KeyboardMouseState
{
    public float2 Move;
    public float2 LookDelta;
    public float Scroll;
    public bool SpaceDown;
    public bool EDown;
    public bool ESCDown;
    public bool LeftMousePressed;
    public bool RightHeld;
    public float2 MousePosition;
}

/// <summary>把原始键鼠状态转换为可预测的玩家输入组件</summary>
public static class PlayerInputFeature
{
    /// <summary>更新相机、鼠标按钮和长按脉冲</summary>
    /// <param name="input">待更新玩家输入</param>
    /// <param name="raw">原始键鼠状态</param>
    /// <param name="context">输入转换上下文</param>
    public static void ApplyMouseInputs(ref PlayerInput input, in KeyboardMouseState raw, in InputContext context)
    {
        input.CameraLookInput = raw.LookDelta;
        input.CameraZoomInput = raw.Scroll;

        if (raw.RightHeld)
        {
            SetRightMouseHeldTimeAndLongPress(ref input, in context);
        }
        else
        {
            input.RightMouseHeld = 0;
            input.RightMouseHeldTime = 0f;
        }

        input.MousePosition = raw.MousePosition;
        if (raw.LeftMousePressed)
            input.LeftMousePressed.Set(context.NetTick);
    }

    private static void SetRightMouseHeldTimeAndLongPress(ref PlayerInput input, in InputContext context)
    {
        input.RightMouseHeld = 1;

        float previousHeldTime = input.RightMouseHeldTime;
        input.RightMouseHeldTime = previousHeldTime + context.DeltaTime;

        // 只在跨过阈值的 Tick 产生一次长按脉冲
        if (previousHeldTime < context.RightLongPressThreshold && input.RightMouseHeldTime >= context.RightLongPressThreshold)
            input.RightMouseLongPress.Set(context.NetTick);
    }

    
    /// <summary>更新移动输入和键盘按键脉冲</summary>
    /// <param name="input">待更新玩家输入</param>
    /// <param name="raw">原始键鼠状态</param>
    /// <param name="context">输入转换上下文</param>
    public static void ApplyKeyboardInput(ref PlayerInput input, in KeyboardMouseState raw, in InputContext context)
    {
        input.MoveInput = raw.Move;

        if (raw.SpaceDown) input.JumpPressed.Set(context.NetTick);
        if (raw.EDown) input.InteractPressed.Set(context.NetTick);
        if (raw.ESCDown) input.PausePressed.Set(context.NetTick);
    }

}
