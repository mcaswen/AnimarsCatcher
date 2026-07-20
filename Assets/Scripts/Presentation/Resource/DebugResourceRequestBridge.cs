using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Presentation.Resource
{
    /// <summary>
    /// 将 Mono 层资源变更事件转发为客户端资源 RPC
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.Resource", "AnimarsCatcher.Presentation", "ChangeResourceNetworkBridge")]
    public class DebugResourceRequestBridge : MonoBehaviour
    {
        private void Start()
        {
            ResourceRequestEvents.AdjustmentRequested.AddListener(OnAdjustmentRequested);
        }

        private void OnDestroy()
        {
            ResourceRequestEvents.AdjustmentRequested.RemoveListener(OnAdjustmentRequested);
        }

        private void OnAdjustmentRequested(ResourceAdjustmentRequest data)
        {
            // 客户端只发送请求 最终 PlayerResourceState 由服务端修改
            ClientDebugResourceRequestSender.RequestAdjustment(data.Kind, data.Amount);
        }
    }
}
