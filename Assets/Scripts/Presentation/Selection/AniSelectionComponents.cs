using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 保存一次屏幕框选拖拽的按下、移动和释放状态
    /// </summary>
    public struct AniSelectionDragState : IComponentData
    {
        public float2 StartScreen;
        public float2 EndScreen;
        public byte IsDragging;
        // 仅在释放发生的帧为一
        public byte IsReleased;
        // 保存上一帧右键状态，用于判断本帧是按下还是释放
        public byte PreviousRightHeld;
    }

    /// <summary>
    /// 客户端 ECS 持有的 UGUI 托管对象引用
    /// </summary>
    public class AniSelectionUIReference : IComponentData
    {
        public Camera WorldCamera;
        public Canvas RootCanvas;
        public RectTransform SelectionRect;
    }

    /// <summary>
    /// 标识框选 UI 引用已经绑定到 ECS
    /// </summary>
    public struct AniSelectionUIAttachedTag : IComponentData {}

    /// <summary>
    /// 当前框选允许的 Ani 类型
    /// </summary>
    public enum AniSelectionMode : byte
    {
        Picker  = 0,
        Blaster = 1,
    }

    /// <summary>
    /// 客户端当前 Ani 选择模式单例
    /// </summary>
    public struct AniSelectionModeState : IComponentData
    {
        public AniSelectionMode Mode;
    }


    /// <summary>
    /// 保存客户端最近提交和服务器已经确认的选择集版本
    /// </summary>
    public struct ClientAniSelectionSetState : IComponentData
    {
        public uint SubmittedVersion;
        public ulong SubmittedHash;
        public int SubmittedMemberCount;
        public uint AcknowledgedVersion;
        public ulong AcknowledgedHash;
        public int AcknowledgedMemberCount;
    }
}
