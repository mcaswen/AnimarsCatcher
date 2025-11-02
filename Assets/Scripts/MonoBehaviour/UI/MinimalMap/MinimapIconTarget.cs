using UnityEngine;
using UnityEngine.UI;

namespace AnimarsCatcher.Mono.UI
{
    public class MinimapIconTarget : MonoBehaviour
    {
        [Header("Minimap Icon")]
        public Sprite iconSprite;
        public Color iconColor = Color.white;
        public Vector3 worldOffset = new Vector3(0f, 1.5f, 0f);
    }
}