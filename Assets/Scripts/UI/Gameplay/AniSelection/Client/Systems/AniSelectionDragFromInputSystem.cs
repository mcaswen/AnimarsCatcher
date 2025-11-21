using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(PlayerInputSystem))]
public partial struct AniSelectionDragFromInputSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<AniSelectionDragState>();
        state.RequireForUpdate(SystemAPI.QueryBuilder().WithAll<PlayerInput>().Build());
    }

    public void OnUpdate(ref SystemState state)
    {
        var drag = SystemAPI.GetSingletonRW<AniSelectionDragState>();

        byte rightHeldNow = 0;
        float2 mousePosition = default;

        foreach (var input in SystemAPI.Query<RefRO<PlayerInput>>())
        {
            rightHeldNow = input.ValueRO.RightMouseHeld;
            mousePosition = input.ValueRO.MousePosition;
            break;
        }

        // 清零 IsReleased，由结束分支置 1
        drag.ValueRW.IsReleased = 0;

        bool previousHeld = drag.ValueRO.PreviousRightHeld == 1;
        bool nowHeld = rightHeldNow == 1;

        // 开始拖拽
        if (!previousHeld && nowHeld)
        {
            drag.ValueRW.IsDragging  = 1;
            drag.ValueRW.StartScreen = mousePosition;
            drag.ValueRW.EndScreen   = mousePosition;
        }
        // 按住时更新终点
        else if (previousHeld && nowHeld && drag.ValueRO.IsDragging == 1)
        {
            drag.ValueRW.EndScreen = mousePosition;
        }
        // 抬起时结束拖拽
        else if (previousHeld && !nowHeld && drag.ValueRO.IsDragging == 1)
        {
            drag.ValueRW.EndScreen = mousePosition;
            drag.ValueRW.IsDragging = 0;
            drag.ValueRW.IsReleased = 1;
        }

        drag.ValueRW.PreviousRightHeld = rightHeldNow;
    }
}
