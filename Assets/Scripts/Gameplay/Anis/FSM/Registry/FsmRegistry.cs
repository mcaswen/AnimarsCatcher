using AnimarsCatcher.Core.Fsm;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.Entities;
using AnimarsCatcher;
using UnityEngine.PlayerLoop;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 定义可由 Burst 函数指针调用的状态迁移条件
    /// </summary>
    public delegate bool ConditionFunction(in Entity entity, in FsmContext context);

    /// <summary>
    /// 定义可由 Burst 函数指针调用的状态动作
    /// </summary>
    public delegate void ActionFunction(in Entity entity, ref Fsm fsm, in FsmContext context);

    /// <summary>
    /// 使用固定索引的 Burst 函数指针表替代运行时多态分派
    /// 注册表生命周期由 FsmRegistryBootstrapSystem 统一管理
    /// </summary>
    [BurstCompile]
    public static class FsmRegistry
    {
        private static NativeArray<FunctionPointer<ConditionFunction>> _conditions;
        private static NativeArray<FunctionPointer<ActionFunction>> _actions;
        private static bool _isAlive = true;
        private static bool _isInitialized;
        private const int MaxConditionCount = 1024;
        private const int MaxActionCount = 1024;

        /// <summary>
        /// 分配持久化条件表和动作表，重复调用不会再次分配
        /// </summary>
        public static void Initialize()
        {
            if (_isInitialized) return;

            _conditions = new NativeArray<FunctionPointer<ConditionFunction>>(MaxConditionCount, Allocator.Persistent);
            _actions = new NativeArray<FunctionPointer<ActionFunction>>(MaxActionCount, Allocator.Persistent);

            _isInitialized = true;
        }
        /// <summary>
        /// 释放注册表持有的原生数组
        /// </summary>
        public static void Dispose()
        {
            if (!_isAlive) return;
            if (_conditions.IsCreated) _conditions.Dispose();
            if (_actions.IsCreated) _actions.Dispose();
            _isAlive = false;
        }

        /// <summary>
        /// 把条件函数指针登记到指定条件标识符
        /// </summary>
        /// <param name="id">条件函数的全局标识符</param>
        /// <param name="conditionFunctionPointer">已由 Burst 编译的条件函数指针</param>
        public static void RegisterCondition(
            ConditionId id,
            FunctionPointer<ConditionFunction> conditionFunctionPointer)
        {
            var conditions = _conditions;
            conditions[(int)id] = conditionFunctionPointer;
        }

        /// <summary>
        /// 把动作函数指针登记到指定动作标识符
        /// </summary>
        /// <param name="id">动作函数的全局标识符</param>
        /// <param name="actionFunctionPointer">已由 Burst 编译的动作函数指针</param>
        public static void RegisterAction(
            ActionId id,
            FunctionPointer<ActionFunction> actionFunctionPointer)
        {
            var actions = _actions;
            actions[(int)id] = actionFunctionPointer;
        }

        /// <summary>
        /// 调用已注册条件，标识符未注册时按不满足处理
        /// </summary>
        /// <param name="id">条件标识符</param>
        /// <param name="entity">正在评估的 Entity</param>
        /// <param name="context">当前状态机上下文</param>
        /// <returns>条件函数的评估结果</returns>
        public static bool InvokeCondition(ConditionId id, in Entity entity, in FsmContext context)
        {
            var fp = _conditions[(int)id];
            if (fp.IsCreated) return fp.Invoke(entity, context);

            // 未注册条件不能触发状态迁移
            return false;
        }

        /// <summary>
        /// 调用已注册动作，标识符未注册时保持状态不变
        /// </summary>
        /// <param name="id">动作标识符</param>
        /// <param name="entity">正在执行动作的 Entity</param>
        /// <param name="fsm">允许动作更新的状态机组件</param>
        /// <param name="context">当前状态机上下文</param>
        public static void InvokeAction(ActionId id, in Entity entity, ref Fsm fsm, in FsmContext context)
        {
            var fp = _actions[(int)id];
            if (fp.IsCreated) fp.Invoke(entity, ref fsm, context);
        }
    }
}
