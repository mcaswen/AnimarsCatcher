using UnityEngine;

/// <summary>
/// 提供血条生成系统使用的相机 Canvas 和实例父节点
/// </summary>
public class HealthHUDBootstrap : MonoBehaviour
{
    [Header("Camera")]
    public Camera worldCamera;

    [Header("Canvas Root")]
    public Canvas canvas;
    public Transform healthBarRoot;
}
