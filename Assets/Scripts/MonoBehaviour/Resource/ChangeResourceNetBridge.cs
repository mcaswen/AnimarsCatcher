using AnimarsCatcher.Mono.Global;
using UnityEngine;

/// <summary>
/// 将 Mono 层资源变更事件转发为客户端资源 RPC
/// </summary>
public class ChangeResourceNetBridge : MonoBehaviour
{
    private void Start()
    {
        NetUIEventBridge.ResourceChangedRequestedEvent.AddListener(OnResourceChangedRequested);
    }

    private void OnDestroy()
    {
        NetUIEventBridge.ResourceChangedRequestedEvent.RemoveListener(OnResourceChangedRequested);
    }

    private void OnResourceChangedRequested(ResourceChangedRequestedEventData data)
    {
        // 客户端只发送请求 最终 PlayerResourceState 由服务端修改
        ClientResourceRpcSender.RequestAddResource(data.ResourceType, data.Amount);
    }
}
