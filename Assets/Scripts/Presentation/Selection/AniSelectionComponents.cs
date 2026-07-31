using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;
using Unity.NetCode;
using Unity.Collections;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 一次屏幕框选拖拽的运行时边沿状态
    /// </summary>
    public struct AniSelectionDragState : IComponentData
    {
        public float2 StartScreen;
        public float2 EndScreen;
        public byte IsDragging;
        public byte IsReleased;  // 仅在释放发生的帧为一
        public byte PreviousRightHeld; // 上一帧右键状态用于检测输入边沿
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
    /// 标识框选 UI 引用已完成注入
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


    #region 网络同步部分

    /// <summary>
    /// 已选 Ani 的 Ghost 标识缓冲元素
    /// </summary>
    public struct SelectedAniGhostReference : IBufferElementData
    {
        public int AniGhostId;
    }

    /// <summary>
    /// 客户端提交的 Ani 选择 GhostId 列表
    /// </summary>
    public struct AniSelectionRequestRpc : IRpcCommand
    {
        public byte Append; // 零表示替换现有选择，非零表示追加选择
        public FixedList512Bytes<int> GhostIds; // 本次请求包含的 GhostId 列表
    }



    #endregion
}
