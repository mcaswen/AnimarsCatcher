using UnityEngine.Events;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 描述 Ani 选择交互模式变化
    /// </summary>
    public readonly struct AniSelectionModeChangedEvent
    {
        public readonly AniSelectionMode Mode;

        public AniSelectionModeChangedEvent(AniSelectionMode mode)
        {
            Mode = mode;
        }
    }

    /// <summary>
    /// 发布表现层 Ani 选择模式变化
    /// </summary>
    public static class AniSelectionEvents
    {
        public static readonly UnityEvent<AniSelectionModeChangedEvent> ModeChanged = new();

        /// <summary>
        /// 发布新的 Ani 选择模式
        /// </summary>
        public static void RaiseModeChanged(AniSelectionMode mode)
            => ModeChanged.Invoke(new AniSelectionModeChangedEvent(mode));
    }
}
