using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;
using Unity.NetCode;
using Unity.Collections;

// 框选的运行时状态
public struct AniSelectionDragState : IComponentData
{
    public float2 StartScreen;   
    public float2 EndScreen;     
    public byte IsDragging;   
    public byte IsReleased;  // 本帧刚刚释放
    public byte PreviousRightHeld; // 上一帧右键是否按住（用于检测抬起）
}

// UGUI 桥接
public class AniSelectionUIRef : IComponentData
{
    public Camera WorldCamera;
    public Canvas RootCanvas;
    public RectTransform SelectionRect;
}

public struct SelectionUIAttachedTag : IComponentData {}

#region 网络同步部分

// Ghost 引用缓冲区
public struct SelectedAniGhostRef : IBufferElementData
{
    public int AniGhostId;
}

// 选择申请 RPC
public struct AniSelectionApplyRpc : IRpcCommand
{
    public byte Append; // 0 = 替换（清空旧选择后再置位），1 = 追加（在已有选择基础上置位）
    public FixedList512Bytes<int> GhostIds; // 存选中的 GhostId 列表
}

#endregion