using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

/// <summary>
/// 描述一条状态迁移的目标、条件和出入动作
/// </summary>
public struct FsmTransition
{
    public StateId To;
    public ConditionId Condition;
    public ActionId OnExit; // 从当前状态离开、沿这条边跳转时要做的退出动作
    public ActionId OnEnter; // 抵达目标状态、沿这条边进入时要做的进入动作
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
    public BlobArray<FsmStateNode> States; // 这里按 StateId 索引
}

/// <summary>
/// 把共享的状态图 Blob 引用附加到运行时实体
/// </summary>
public struct FsmGraphRef : IComponentData
{
    public BlobAssetReference<FsmGraph> Value;
}
