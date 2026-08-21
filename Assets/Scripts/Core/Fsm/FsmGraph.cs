using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace AnimarsCatcher.Core.Fsm
{
    /// <summary>
    /// 描述一条状态迁移的目标、条件和出入动作
    /// </summary>
    public struct FsmTransition
    {
        public StateId To;
        public ConditionId Condition;
        // 仅在采用这条迁移边时执行
        public ActionId OnExit;
        public ActionId OnEnter;
    }

    /// <summary>
    /// 保存状态标识、可选迁移边和持续更新动作
    /// </summary>
    public struct FsmStateNode
    {
        public StateId State;
        public BlobArray<FsmTransition> Transitions;
        public ActionId OnUpdate;
    }

    /// <summary>
    /// 使用 StateId 作为索引保存不可变状态节点数组
    /// </summary>
    public struct FsmGraph
    {
        // 数组下标与 StateId 数值一一对应
        public BlobArray<FsmStateNode> States;
    }

    /// <summary>
    /// 把共享的状态图 Blob 引用附加到运行时 Entity
    /// </summary>
    public struct FsmGraphRef : IComponentData
    {
        public BlobAssetReference<FsmGraph> Value;
    }
}
