using UnityEngine;

/// <summary>
/// 为客户端点击射线提供相机和各目标类别的物理层掩码
/// </summary>
public class MovementRaycastBootstrap : MonoBehaviour
{
    public Camera WorldCamera;
    public LayerMask PlayerMask;
    public LayerMask GroundMask;
    public LayerMask AniMask;
    public LayerMask ResourceMask;
    public LayerMask BaseMask;
}
