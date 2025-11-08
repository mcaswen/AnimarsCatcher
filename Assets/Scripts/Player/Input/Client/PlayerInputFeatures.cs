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

    
    public static void ApplyKeyboardInput(ref PlayerInput input, in KeyBoardMouseState raw, in InputContext context)
    {
        input.MoveInput = raw.Move;

        if (raw.SpaceDown) input.JumpPressed.Set(context.NetTick);
        if (raw.EDown) input.InteractPressed.Set(context.NetTick);
        if (raw.ESCDown) input.PausePressed.Set(context.NetTick);
    }

}