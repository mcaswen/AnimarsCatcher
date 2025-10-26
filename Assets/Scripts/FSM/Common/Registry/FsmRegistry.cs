using Unity.Burst;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.Entities;
using AnimarsCatcher;

public delegate bool ConditionFn(ref Fsm fsm, ref DynamicBuffer<FsmVar> bb, in FsmContext context);
public delegate void ActionFn(ref Fsm fsm, ref DynamicBuffer<FsmVar> bb, in FsmContext context);

// 线程安全静态表(替代接口的虚函数表，实现更优性能，契合 DOTS 设计理念)
public static class FsmRegistry
{
    static NativeArray<FunctionPointer<ConditionFn>> s_Conditions;
    static NativeArray<FunctionPointer<ActionFn>>    s_Actions;
    static bool s_Inited;
    const int MAX_CONDITION = 1024;
    const int MAX_ACTION = 1024;

    public static void InitIfNeeded(int maxCondition = MAX_CONDITION, int maxAction = MAX_ACTION, Allocator allocator = Allocator.Persistent)
    {
        if (s_Inited) return;
        s_Conditions = new NativeArray<FunctionPointer<ConditionFn>>(maxCondition, allocator);
        s_Actions = new NativeArray<FunctionPointer<ActionFn>>(maxAction, allocator);
        s_Inited = true;
    }

    //手动释放内存
    public static void Dispose()
    {
        if (!s_Inited) return;
        if (s_Conditions.IsCreated) s_Conditions.Dispose();
        if (s_Actions.IsCreated)  s_Actions.Dispose();
        s_Inited = false;
    }

    public static void RegisterCondition(ConditionId id, FunctionPointer<ConditionFn> fn)
    {
        s_Conditions[(int)id] = fn;
    }

    public static void RegisterAction(ActionId id, FunctionPointer<ActionFn> fn)
    {
        s_Actions[(int)id] = fn;
    }

    [BurstCompile]
    public static bool InvokeCondition(ConditionId id, ref Fsm fsm, ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        var fp = s_Conditions[(int)id];
        if (fp.IsCreated) return fp.Invoke(ref fsm, ref blackboard, context);
        return false; // 未注册则视为不满足
    }

    [BurstCompile]
    public static void InvokeAction(ActionId id, ref Fsm fsm, ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        var fp = s_Actions[(int)id];
        if (fp.IsCreated) fp.Invoke(ref fsm, ref blackboard, context);
    }
}
