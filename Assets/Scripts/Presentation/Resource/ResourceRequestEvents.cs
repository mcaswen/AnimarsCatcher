using AnimarsCatcher.Gameplay.Contracts;
using UnityEngine.Events;

namespace AnimarsCatcher.Presentation.Resource
{
    /// <summary>
    /// 描述客户端发起的调试资源调整请求
    /// </summary>
    public readonly struct ResourceAdjustmentRequest
    {
        public readonly ResourceItemKind Kind;
        public readonly int Amount;

        public ResourceAdjustmentRequest(ResourceItemKind kind, int amount)
        {
            Kind = kind;
            Amount = amount;
        }
    }

    /// <summary>
    /// 发布表现层发起的资源调整请求
    /// </summary>
    public static class ResourceRequestEvents
    {
        public static readonly UnityEvent<ResourceAdjustmentRequest> AdjustmentRequested = new();

        /// <summary>
        /// 发布资源调整请求
        /// </summary>
        public static void RaiseAdjustmentRequested(
            ResourceItemKind kind,
            int amount)
        {
            AdjustmentRequested.Invoke(new ResourceAdjustmentRequest(kind, amount));
        }
    }
}
