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
}