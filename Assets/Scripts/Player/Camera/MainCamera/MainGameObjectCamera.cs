namespace AnimarsCatcher.Player
{
    using UnityEngine;

    /// <summary>
    /// 提供客户端主相机组件的运行时入口
    /// </summary>
    public class MainGameObjectCamera : MonoBehaviour
    {
        public static Camera Instance { get; private set; }

        private void Awake()
        {
            Instance = GetComponent<Camera>();
        }

        private void OnDestroy()
        {
            if (Instance == GetComponent<Camera>())
            {
                Instance = null;
            }
        }
    }
}
