using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using AnimarsCatcher.Player;

/// <summary>
/// 将右键按压边沿转换为框选拖拽状态
/// </summary>
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

        // 释放标记只保持一帧 由结束分支重新置位
        drag.ValueRW.IsReleased = 0;

        bool previousHeld = drag.ValueRO.PreviousRightHeld == 1;
        bool nowHeld = rightHeldNow == 1;

        // 按下边沿同时记录框选起点和当前终点
        if (!previousHeld && nowHeld)
        {
            drag.ValueRW.IsDragging  = 1;
            drag.ValueRW.StartScreen = mousePosition;
            drag.ValueRW.EndScreen   = mousePosition;
        }
        // 持续按住时只更新终点
        else if (previousHeld && nowHeld && drag.ValueRO.IsDragging == 1)
        {
            drag.ValueRW.EndScreen = mousePosition;
        }
        // 释放边沿结束拖拽并通知 RPC 系统消费结果
        else if (previousHeld && !nowHeld && drag.ValueRO.IsDragging == 1)
        {
            drag.ValueRW.EndScreen = mousePosition;
            drag.ValueRW.IsDragging = 0;
            drag.ValueRW.IsReleased = 1;
        }

        drag.ValueRW.PreviousRightHeld = rightHeldNow;
    }
}
