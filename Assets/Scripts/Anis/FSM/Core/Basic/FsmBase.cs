using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

// Id
public enum StateId : ushort { S0 = 0 }                     // 占位符
public enum ConditionId : ushort { None = 0 }               // 0 代表永不触发
public enum ActionId : ushort { None = 0 }                  // 0 代表空操作

// 运行时 Fsm 组件
public struct Fsm : IComponentData
{
    public StateId Current;
    public StateId Next;

    public float TimeInState;   // 秒
    public byte HasPending;
    
    public ActionId PendingExit;
    public ActionId PendingEnter;
}

// 运行时 Fsm 上下文
public struct FsmContext : IComponentData
{
    public float DeltaTime;
    public uint Tick;
    public BufferLookup<FsmVar> BlackboardLookup;
}

//划分 id 空间，避免与其它模块冲突
public static class FsmIdSpace
{
    public const ushort Block = 256;

    public const ushort AniMovementBase  = Block * 1; 
    public const ushort PickerAniBase = Block * 2; 

    public static ushort Of(ushort @base, ushort local) => (ushort)(@base + local);
}