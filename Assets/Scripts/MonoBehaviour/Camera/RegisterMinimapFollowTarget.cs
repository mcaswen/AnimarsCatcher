using UnityEngine;

namespace AnimarsCatcher.Mono
{
    /// <summary>
    /// 当本地玩家视图准备好时，把自己注册给 MinimapCameraFollower。
    /// </summary>
    public class RegisterMinimapFollowTarget : MonoBehaviour
    {
        [Tooltip("是否在 Start 时自动注册为小地图跟随目标")]
        public bool AutoRegisterOnStart = true;

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
        /// 如果你在别处判断“这个玩家是本地玩家”，可以在那边手动调用。
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
