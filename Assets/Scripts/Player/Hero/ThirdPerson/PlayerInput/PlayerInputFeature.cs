using Unity.Mathematics;

public readonly struct InputContext
{
    public readonly float DeltaTime;
    public readonly uint NetTick;
    public readonly float RightLongPressThreshold;

    public InputContext(float deltaTime, uint tick, float longPressThreshold)
    {
        DeltaTime = deltaTime; NetTick = tick; RightLongPressThreshold = longPressThreshold;
    }
}

public struct KeyBoardMouseState
{
    public float2 Move;
    public float2 LookDelta;
    public float Scroll;
    public bool SpaceDown;
    public bool EDown;
    public bool ESCDown;
    public bool RightHeld;
    public float2 MousePos;
}

public static class PlayerInputFeature
{
    public static void ApplyMouseInputs(ref PlayerInput input, in KeyBoardMouseState raw, in InputContext context)
    {
        input.CameraLookInput = raw.LookDelta;
        input.CameraZoomInput = raw.Scroll;

        if (raw.RightHeld)
        {
            SetRightMouseHeldTimeAndLongPress(ref input, in context);
            SetRightMouseHeldTick(input.RightMouseHeld, ref input, in context); // 预测端，从按下那一帧开始计算
        }
        else
        {
            input.RightMouseHeld = 0;
            input.RightMouseHeldTime = 0f;
        }

        input.MousePosition = raw.MousePos;
    }

    private static void SetRightMouseHeldTimeAndLongPress(ref PlayerInput input, in InputContext context)
    {
        input.RightMouseHeld = 1;

        float previousHeldTime = input.RightMouseHeldTime;
        input.RightMouseHeldTime = previousHeldTime + context.DeltaTime;

        // 本地端，直接使用阈值计算
        if (previousHeldTime < context.RightLongPressThreshold && input.RightMouseHeldTime >= context.RightLongPressThreshold)
            input.RightMouseLongPress.Set(context.NetTick);

    }
    
    private static void SetRightMouseHeldTick(byte wasHeld, ref PlayerInput input, in InputContext context)
    {
        if (wasHeld == 0)
        {
            input.RightMouseHoldStartTick = context.NetTick;
            input.RightMouseHeldTicks = 0; // 刚按下，计数从0开始
        }
        else
        {
            uint held = context.NetTick - input.RightMouseHoldStartTick;
            input.RightMouseHeldTicks = (ushort)math.min(held, (uint)ushort.MaxValue);
        }
    }

    
    public static void ApplyKeyboardInput(ref PlayerInput input, in KeyBoardMouseState raw, in InputContext context)
    {
        input.MoveInput = raw.Move;

        if (raw.SpaceDown) input.JumpPressed.Set(context.NetTick);
        if (raw.EDown) input.InteractPressed.Set(context.NetTick);
        if (raw.ESCDown) input.PausePressed.Set(context.NetTick);
    }

}