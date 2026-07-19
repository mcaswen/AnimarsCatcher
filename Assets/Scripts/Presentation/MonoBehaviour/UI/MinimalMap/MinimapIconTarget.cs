using UnityEngine;
using UnityEngine.UI;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 声明可显示在小地图上的场景目标及其图标样式
    /// </summary>
    public class MinimapIconTarget : MonoBehaviour
    {
        [Header("Minimap Icon")]
        public Sprite iconSprite;
        public Color iconColor = Color.white;
        public Vector3 worldOffset = new Vector3(0f, 1.5f, 0f);
    }
}
