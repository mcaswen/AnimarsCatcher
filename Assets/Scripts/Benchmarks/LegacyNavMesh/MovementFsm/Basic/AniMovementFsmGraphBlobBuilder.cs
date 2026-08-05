using AnimarsCatcher.Core.Fsm;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using System.Runtime.InteropServices;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 构建移动状态机的不可变 Blob 图和有序迁移边
    /// </summary>
    public static class AniMovementFsmGraphBlobBuilder
    {
        private const int MaxCapacity = 1024;

        /// <summary>
        /// 创建状态图根节点并按全局状态标识符预分配节点数组
        /// </summary>
        /// <param name="builder">返回临时 Blob 构建器</param>
        /// <param name="states">返回可写状态节点数组</param>
        public static void AllocateBuilderBase(out BlobBuilder builder, out BlobBuilderArray<FsmStateNode> states)
        {
            builder = new BlobBuilder(Allocator.Temp);
            ref var graph = ref builder.ConstructRoot<FsmGraph>();
            states = builder.Allocate(ref graph.States, MaxCapacity);
        }

        /// <summary>
        /// 构建空闲状态到跟随、寻敌和定点移动的迁移边
        /// </summary>
        /// <param name="builder">状态图 Blob 构建器</param>
        /// <param name="states">可写状态节点数组</param>
        public static void BuildIdleState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
        {
            states[AniMovementFsmIds.IdleStateId].State = (StateId)AniMovementFsmIds.IdleStateId;
            var transitions = builder.Allocate(ref states[AniMovementFsmIds.IdleStateId].Transitions, 3);

            // 从 Idle 转到 Follow
            transitions[0] = new FsmTransition
            {
                To        = (StateId)AniMovementFsmIds.FollowStateId,
                Condition = (ConditionId)AniMovementFsmIds.CommandFollowConditionId,
                OnEnter   = (ActionId)AniMovementFsmIds.EnterFollowActionId,
                OnExit    = (ActionId)AniMovementFsmIds.ExitIdleActionId,
            };

            // 从 Idle 转到 Find
            transitions[1] = new FsmTransition
            {
                To        = (StateId)AniMovementFsmIds.FindStateId,
                Condition = (ConditionId)AniMovementFsmIds.CommandFindConditionId,
                OnEnter   = (ActionId)AniMovementFsmIds.EnterFindActionId,
                OnExit    = (ActionId)AniMovementFsmIds.ExitIdleActionId,
            };

            // 从 Idle 转到 MoveTo
            transitions[2] = new FsmTransition
            {
                To        = (StateId)AniMovementFsmIds.MoveToStateId,
                Condition = (ConditionId)AniMovementFsmIds.CommandMoveToConditionId,
                OnEnter   = (ActionId)AniMovementFsmIds.EnterMoveToActionId,
                OnExit    = (ActionId)AniMovementFsmIds.ExitIdleActionId,
            };
        }

        /// <summary>
        /// 构建跟随状态到寻敌和定点移动的迁移边
        /// </summary>
        /// <param name="builder">状态图 Blob 构建器</param>
        /// <param name="states">可写状态节点数组</param>
        public static void BuildFollowState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
        {
            states[AniMovementFsmIds.FollowStateId].State = (StateId)AniMovementFsmIds.FollowStateId;
            var transitions = builder.Allocate(ref states[AniMovementFsmIds.FollowStateId].Transitions, 2);

            // 从 Follow 转到 Find
            transitions[0] = new FsmTransition
            {
                To        = (StateId)AniMovementFsmIds.FindStateId,
                Condition = (ConditionId)AniMovementFsmIds.CommandFindConditionId,
                OnEnter   = (ActionId)AniMovementFsmIds.EnterFindActionId,
                OnExit    = (ActionId)AniMovementFsmIds.ExitFollowActionId,
            };

            // 从 Follow 转到 MoveTo
            transitions[1] = new FsmTransition
            {
                To        = (StateId)AniMovementFsmIds.MoveToStateId,
                Condition = (ConditionId)AniMovementFsmIds.CommandMoveToConditionId,
                OnEnter   = (ActionId)AniMovementFsmIds.EnterMoveToActionId,
                OnExit    = (ActionId)AniMovementFsmIds.ExitFollowActionId,
            };
        }

        /// <summary>
        /// 构建寻敌状态到跟随、定点移动和空闲的迁移边
        /// </summary>
        /// <param name="builder">状态图 Blob 构建器</param>
        /// <param name="states">可写状态节点数组</param>
        public static void BuildFindState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
        {
            states[AniMovementFsmIds.FindStateId].State = (StateId)AniMovementFsmIds.FindStateId;
            var transitions = builder.Allocate(ref states[AniMovementFsmIds.FindStateId].Transitions, 3);

            // 收到 CommandFollow 时从 Find 转到 Follow
            transitions[0] = new FsmTransition
            {
                To        = (StateId)AniMovementFsmIds.FollowStateId,
                Condition = (ConditionId)AniMovementFsmIds.CommandFollowConditionId,
                OnEnter   = (ActionId)AniMovementFsmIds.EnterFollowActionId,
                OnExit    = (ActionId)AniMovementFsmIds.ExitFindActionId,
            };

            // 收到 CommandMoveTo 时从 Find 转到 MoveTo
            transitions[1] = new FsmTransition
            {
                To        = (StateId)AniMovementFsmIds.MoveToStateId,
                Condition = (ConditionId)AniMovementFsmIds.CommandMoveToConditionId,
                OnEnter   = (ActionId)AniMovementFsmIds.EnterMoveToActionId,
                OnExit    = (ActionId)AniMovementFsmIds.ExitFindActionId,
            };

            // 目标消失时从 Find 转到 Idle
            transitions[2] = new FsmTransition
            {
                To        = (StateId)AniMovementFsmIds.IdleStateId,
                Condition = (ConditionId)AniMovementFsmIds.TargetGoneConditionId,
                OnEnter   = (ActionId)AniMovementFsmIds.EnterIdleActionId,
                OnExit    = (ActionId)AniMovementFsmIds.ExitFindActionId,
            };
        }

        /// <summary>
        /// 构建定点移动状态到空闲、跟随和寻敌的迁移边
        /// </summary>
        /// <param name="builder">状态图 Blob 构建器</param>
        /// <param name="states">可写状态节点数组</param>
        public static void BuildMoveToState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
        {
            states[AniMovementFsmIds.MoveToStateId].State = (StateId)AniMovementFsmIds.MoveToStateId;
            var transitions = builder.Allocate(ref states[AniMovementFsmIds.MoveToStateId].Transitions, 3);

            // 到达目标时从 MoveTo 转到 Idle
            transitions[0] = new FsmTransition
            {
                To        = (StateId)AniMovementFsmIds.IdleStateId,
                Condition = (ConditionId)AniMovementFsmIds.MoveArrivedConditionId,
                OnEnter   = (ActionId)AniMovementFsmIds.EnterIdleActionId,
                OnExit    = (ActionId)AniMovementFsmIds.ExitMoveToActionId,
            };

            // 从 MoveTo 转到 Follow
            transitions[1] = new FsmTransition
            {
                To        = (StateId)AniMovementFsmIds.FollowStateId,
                Condition = (ConditionId)AniMovementFsmIds.CommandFollowConditionId,
                OnEnter   = (ActionId)AniMovementFsmIds.EnterFollowActionId,
                OnExit    = (ActionId)AniMovementFsmIds.ExitMoveToActionId,
            };

            // 从 MoveTo 转到 Find
            transitions[2] = new FsmTransition
            {
                To        = (StateId)AniMovementFsmIds.FindStateId,
                Condition = (ConditionId)AniMovementFsmIds.CommandFindConditionId,
                OnEnter   = (ActionId)AniMovementFsmIds.EnterFindActionId,
                OnExit    = (ActionId)AniMovementFsmIds.ExitMoveToActionId,
            };
        }
    }
}
