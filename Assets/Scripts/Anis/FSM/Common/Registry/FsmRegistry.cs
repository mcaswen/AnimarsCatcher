using Unity.Burst;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.Entities;
using AnimarsCatcher;
using UnityEngine.PlayerLoop;

public delegate bool ConditionFn(in Entity entity, in FsmContext context);
public delegate void ActionFn(in Entity entity, ref Fsm fsm, in FsmContext context);

// 线程安全静态表(替代接口的虚函数表，实现更优性能，契合 DOTS 设计理念)
[BurstCompile]
public static class FsmRegistry
{
    private static NativeArray<FunctionPointer<ConditionFn>> s_Conditions;
    private static NativeArray<FunctionPointer<ActionFn>> s_Actions;
    private static bool s_Alive = true;
    private static bool s_Initialized = false;
    private const int MAX_CONDITION = 1024;
    private const int MAX_ACTION = 1024;

    public static void Init()
    {
        if (s_Initialized) return;

        s_Conditions  = new NativeArray<FunctionPointer<ConditionFn>>(MAX_CONDITION, Allocator.Persistent);
        s_Actions = new NativeArray<FunctionPointer<ActionFn>>(MAX_ACTION, Allocator.Persistent);

        s_Initialized = true;
    }


    // 手动释放内存
    // [BurstCompile]
    public static void Dispose()
    {
        if (!s_Alive) return;
        if (s_Conditions.IsCreated) s_Conditions.Dispose();
        if (s_Actions.IsCreated) s_Actions.Dispose();
        s_Alive = false;
    }

    // [BurstCompile]
    public static void RegisterCondition(ConditionId id, FunctionPointer<ConditionFn> fn)
    {
        var conditions = s_Conditions;
        conditions[(int)id] = fn;
    }

    // [BurstCompile]
    public static void RegisterAction(ActionId id, FunctionPointer<ActionFn> fn)
    {
        var actions = s_Actions;
        actions[(int)id] = fn;
    }

    // [BurstCompile]
    public static bool InvokeCondition(ConditionId id, in Entity entity, in FsmContext context)
    {
        var fp = s_Conditions[(int)id];
        if (fp.IsCreated) return fp.Invoke(entity, context);
        return false; // 未注册则视为不满足
    }

    // [BurstCompile]
    public static void InvokeAction(ActionId id, in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var fp = s_Actions[(int)id];
        if (fp.IsCreated) fp.Invoke(entity, ref fsm, context);
    }
}
