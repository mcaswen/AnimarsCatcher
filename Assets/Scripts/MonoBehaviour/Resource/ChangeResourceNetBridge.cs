using AnimarsCatcher.Mono.Global;
using UnityEngine;

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
        // 由服务器修改 PlayerResourceState
        ClientResourceRpcSender.RequestAddResource(data.ResourceType, data.Amount);
    }
}
