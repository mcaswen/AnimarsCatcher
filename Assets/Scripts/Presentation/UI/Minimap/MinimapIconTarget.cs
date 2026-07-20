using UnityEngine;
using UnityEngine.Serialization;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 声明可显示在小地图上的场景目标及其图标样式
    /// </summary>
    public class MinimapIconTarget : MonoBehaviour
    {
        [Header("Minimap Icon")]
        [FormerlySerializedAs("iconSprite")]
        [SerializeField] private Sprite _iconSprite;
        [FormerlySerializedAs("iconColor")]
        [SerializeField] private Color _iconColor = Color.white;
        [FormerlySerializedAs("worldOffset")]
        [SerializeField] private Vector3 _worldOffset = new Vector3(0f, 1.5f, 0f);

        public Sprite IconSprite => _iconSprite;
        public Color IconColor => _iconColor;
        public Vector3 WorldOffset => _worldOffset;
    }
}
