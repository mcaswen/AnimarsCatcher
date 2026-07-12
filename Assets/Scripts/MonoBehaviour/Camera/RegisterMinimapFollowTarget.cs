using UnityEngine;

namespace AnimarsCatcher.Mono
{
    /// <summary>
    /// 在本地玩家视图就绪后注册小地图跟随目标
    /// </summary>
    public class RegisterMinimapFollowTarget : MonoBehaviour
    {
        public bool AutoRegisterOnStart = true;

        // 等待其他对象完成 Awake 后再查找小地图跟随器
        private void Start()
        {
            if (!AutoRegisterOnStart)
                return;

            if (MinimapCameraFollower.Instance != null)
            {
                MinimapCameraFollower.Instance.BindTarget(transform);
            }
        }

        /// <summary>
        /// 由外部本地玩家判定流程手动注册当前 Transform
        /// </summary>
        public void RegisterManually()
        {
            if (MinimapCameraFollower.Instance != null)
            {
                MinimapCameraFollower.Instance.BindTarget(transform);
            }
        }
    }
}
