using UnityEngine;

namespace AnimarsCatcher.Mono
{
    [DisallowMultipleComponent]
    public class MinimapCameraFollower : MonoBehaviour
    {
        /// <summary>
        /// 场景里唯一的小地图相机跟随器实例
        /// </summary>
        public static MinimapCameraFollower Instance { get; private set; }

        [Header("跟随目标")]
        [SerializeField]
        private Transform _followTarget;

        [Header("高度控制")]
        [Tooltip("小地图相机固定高度（世界坐标 Y）")]
        public float height = 40f;

        [Header("旋转控制")]
        [Tooltip("是否复制目标的 Yaw，让小地图随玩家朝向旋转")]
        public bool copyTargetYaw = false;

        private void Awake()
        {
            // 简单单例：场景里只留一个
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        private void LateUpdate()
        {
            // 优先使用“显式绑定”的目标
            if (_followTarget == null)
            {
                // 如果你还在用 Tag=Player，也可以保留这一段兜底
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

            Vector3 tPos = _followTarget.position;

            // ✅ 关键：始终在玩家正上方，高度固定
            transform.position = new Vector3(tPos.x, height, tPos.z);

            // ✅ 俯视角度
            if (copyTargetYaw)
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
        /// 方便 ECS / 其它 Mono 手动绑定。
        /// </summary>
        public void BindTarget(Transform target)
        {
            _followTarget = target;
        }
    }
}
