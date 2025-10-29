using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

public struct FsmTransition
{
    public StateId To;
    public ConditionId Condition;
    public ActionId OnExit;
    public ActionId OnEnter;
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