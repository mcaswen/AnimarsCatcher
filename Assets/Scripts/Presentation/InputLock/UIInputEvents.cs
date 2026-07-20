using UnityEngine.Events;

namespace AnimarsCatcher.Presentation.InputLock
{
    /// <summary>
    /// 描述 UI 输入锁计数的单次变化
    /// </summary>
    public readonly struct UIInputLockDelta
    {
        public readonly int Value;

        public UIInputLockDelta(int value)
        {
            Value = value;
        }
    }

    /// <summary>
    /// 发布 UI 对玩家输入锁的增减请求
    /// </summary>
    public static class UIInputEvents
    {
        public static readonly UnityEvent<UIInputLockDelta> LockDeltaChanged = new();

        /// <summary>
        /// 增加一层 UI 输入锁
        /// </summary>
        public static void RaiseLocked()
            => LockDeltaChanged.Invoke(new UIInputLockDelta(1));

        /// <summary>
        /// 释放一层 UI 输入锁
        /// </summary>
        public static void RaiseUnlocked()
            => LockDeltaChanged.Invoke(new UIInputLockDelta(-1));
    }
}
