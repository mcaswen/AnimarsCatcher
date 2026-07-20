using UnityEngine;
using UnityEngine.Serialization;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 承载客户端框选功能需要注入 ECS 的场景 UI 引用
    /// </summary>
    public class AniSelectionUIBootstrap : MonoBehaviour
    {
        [FormerlySerializedAs("worldCamera")]
        [SerializeField] private Camera _worldCamera;
        [FormerlySerializedAs("rootCanvas")]
        [SerializeField] private Canvas _rootCanvas;
        [FormerlySerializedAs("selectionRect")]
        [SerializeField] private RectTransform _selectionRect;

        public Camera WorldCamera => _worldCamera;
        public Canvas RootCanvas => _rootCanvas;
        public RectTransform SelectionRect => _selectionRect;
    }
}
