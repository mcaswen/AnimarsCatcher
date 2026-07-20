using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Presentation.HealthBars
{
    /// <summary>
    /// 提供血条生成系统使用的相机 Canvas 和实例父节点
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.HealthUI", "AnimarsCatcher.Presentation", "HealthHUDBootstrap")]
    public class HealthHUDBootstrap : MonoBehaviour
    {
        [Header("Camera")]
        [FormerlySerializedAs("worldCamera")]
        [SerializeField] private Camera _worldCamera;

        [Header("Canvas Root")]
        [FormerlySerializedAs("canvas")]
        [SerializeField] private Canvas _canvas;
        [FormerlySerializedAs("healthBarRoot")]
        [SerializeField] private Transform _healthBarRoot;

        public Camera WorldCamera => _worldCamera;
        public Canvas Canvas => _canvas;
        public Transform HealthBarRoot => _healthBarRoot;
    }
}
