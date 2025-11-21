using Unity.Entities;
using UnityEngine;
using Unity.NetCode;

// 用于承载托管UI对象引用
public class AniSelectionUIBootstrap : MonoBehaviour
{
    public Camera worldCamera;
    public Canvas rootCanvas;
    public RectTransform selectionRect;
}