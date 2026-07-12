using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

/// <summary>
/// 表示状态图中的状态索引，零值保留为通用占位状态
/// </summary>
public enum StateId : ushort { None = 0 }

/// <summary>
/// 表示注册表中的条件函数索引，零值表示永不满足
/// </summary>
public enum ConditionId : ushort { None = 0 }

/// <summary>
/// 表示注册表中的动作函数索引，零值表示空操作
/// </summary>
public enum ActionId : ushort { None = 0 }

/// <summary>
/// 保存实体当前状态、待迁移状态和迁移动作
/// </summary>
public struct Fsm : IComponentData
{
    public StateId Current;
    public StateId Next;

    public float TimeInState;   // 秒
    public byte HasPending;
    
    public ActionId PendingExit;
    public ActionId PendingEnter;
}

/// <summary>
/// 保存本帧状态机时间、Tick 和实体黑板查询
/// </summary>
public struct FsmContext : IComponentData
{
    public float DeltaTime;
    public uint Tick;
    public BufferLookup<FsmVar> BlackboardLookup;
}

/// <summary>
/// 为不同业务状态机划分互不重叠的标识符区间
/// </summary>
public static class FsmIdSpace
{
    public const ushort Block = 256;

    public const ushort AniMovementBase  = Block * 1; 
    public const ushort PickerAniBase = Block * 2; 

    /// <summary>
    /// 将模块基址和局部索引组合为全局状态机标识符
    /// </summary>
    /// <param name="base">模块标识符基址</param>
    /// <param name="local">模块内部局部索引</param>
    /// <returns>全局唯一的标识符</returns>
    public static ushort Of(ushort @base, ushort local) => (ushort)(@base + local);
}
