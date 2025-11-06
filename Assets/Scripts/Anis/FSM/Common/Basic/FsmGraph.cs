using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

public struct FsmTransition
{
    public StateId To;
    public ConditionId Condition;
    public ActionId OnExit; // 从当前状态离开、沿这条边跳转时要做的退出动作
    public ActionId OnEnter; // 抵达目标状态、沿这条边进入时要做的进入动作
}

public struct FsmStateNode
{
    public StateId State;
    public BlobArray<FsmTransition> Transitions;
    public ActionId OnUpdate;    
}

public struct FsmGraph
{
    public BlobArray<FsmStateNode> States; // 这里按 StateId 索引
}

public struct FsmGraphRef : IComponentData
{
    public BlobAssetReference<FsmGraph> Value;
}