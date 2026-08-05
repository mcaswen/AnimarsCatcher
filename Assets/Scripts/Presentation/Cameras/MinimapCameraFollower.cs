using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Presentation.Cameras
{
    /// <summary>
    /// 将小地图相机保持在目标正上方并可选同步目标朝向
    /// </summary>
    [DisallowMultipleComponent]
    [MovedFrom(true, "AnimarsCatcher.Presentation", "AnimarsCatcher.Presentation", "MinimapCameraFollower")]
    public class MinimapCameraFollower : MonoBehaviour
    {
        public static MinimapCameraFollower Instance { get; private set; }

        [Header("跟随目标")]
        [SerializeField]
        private Transform _followTarget;

        [Header("高度控制")]
        [Tooltip("相机固定的世界坐标 Y 值")]
        [FormerlySerializedAs("height")]
        [SerializeField] private float _height = 40f;

        [Header("旋转控制")]
        [Tooltip("启用后随目标朝向旋转，关闭时固定北向")]
        [FormerlySerializedAs("copyTargetYaw")]
        [SerializeField] private bool _copyTargetYaw;

        // 保证场景内只有一个可供玩家视图注册的跟随器
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        // 在目标完成移动后更新相机，减少画面抖动
        private void LateUpdate()
        {
            // 显式绑定优先 Tag 查询仅作为旧场景兼容路径
            if (_followTarget == null)
            {
                GameObject playerObj = GameObject.FindWithTag("Player");
                if (playerObj != null)
                {
                    _followTarget = playerObj.transform;
                }
                else
                {
                    return;
                }
            }

            Vector3 targetPosition = _followTarget.position;

            // 固定世界高度并只跟随目标的水平位置
            transform.position = new Vector3(targetPosition.x, _height, targetPosition.z);

            // 按配置选择固定北向或同步目标偏航角
            if (_copyTargetYaw)
            {
                float yaw = _followTarget.eulerAngles.y;
                transform.rotation = Quaternion.Euler(90f, yaw, 0f);
            }
            else
            {
                transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }
        }

        /// <summary>
        /// 绑定需要跟随的本地玩家视图
        /// </summary>
        /// <param name="target">小地图相机跟随目标</param>
        public void BindTarget(Transform target)
        {
            _followTarget = target;
        }
    }
}
