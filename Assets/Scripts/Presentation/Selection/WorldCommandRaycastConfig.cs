using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 为客户端点击射线提供相机和各目标类别的物理层掩码
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.Selection", "AnimarsCatcher.Presentation", "MovementRaycastBootstrap")]
    public class WorldCommandRaycastConfig : MonoBehaviour
    {
        [FormerlySerializedAs("WorldCamera")]
        [SerializeField] private Camera _worldCamera;
        [FormerlySerializedAs("PlayerMask")]
        [SerializeField] private LayerMask _playerMask;
        [FormerlySerializedAs("GroundMask")]
        [SerializeField] private LayerMask _groundMask;
        [FormerlySerializedAs("AniMask")]
        [SerializeField] private LayerMask _aniMask;
        [FormerlySerializedAs("ResourceMask")]
        [SerializeField] private LayerMask _resourceMask;
        [FormerlySerializedAs("BaseMask")]
        [SerializeField] private LayerMask _baseMask;

        public Camera WorldCamera => _worldCamera;
        public LayerMask PlayerMask => _playerMask;
        public LayerMask GroundMask => _groundMask;
        public LayerMask AniMask => _aniMask;
        public LayerMask ResourceMask => _resourceMask;
        public LayerMask BaseMask => _baseMask;
    }
}
