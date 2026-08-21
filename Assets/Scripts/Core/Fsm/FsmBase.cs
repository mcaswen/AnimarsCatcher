using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace AnimarsCatcher.Core.Fsm
{
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
    /// 保存 Entity 当前状态、待迁移状态和迁移动作
    /// </summary>
    public struct Fsm : IComponentData
    {
        public StateId Current;
        public StateId Next;

        // 当前状态累计时间，单位为秒
        public float TimeInState;
        public byte HasPending;

        public ActionId PendingExit;
        public ActionId PendingEnter;
    }

    /// <summary>
    /// 保存本帧状态机时间、Tick 和 Entity 黑板查询
    /// </summary>
    public struct FsmContext : IComponentData
    {
        public float DeltaTime;
        public uint Tick;
        public BufferLookup<FsmVar> BlackboardLookup;
    }
}
