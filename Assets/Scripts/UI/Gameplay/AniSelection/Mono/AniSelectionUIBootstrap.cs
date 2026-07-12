using Unity.Entities;
using UnityEngine;
using Unity.NetCode;

/// <summary>
/// 承载客户端框选功能需要注入 ECS 的场景 UI 引用
/// </summary>
public class AniSelectionUIBootstrap : MonoBehaviour
{
    public Camera worldCamera;
    public Canvas rootCanvas;
    public RectTransform selectionRect;
}
