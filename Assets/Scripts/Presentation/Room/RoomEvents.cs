namespace AnimarsCatcher.Presentation.Room
{
    /// <summary>
    /// 标记可通过 PresentationEventBus 发布的表现事件
    /// </summary>
    public interface IPresentationEvent { }

    /// <summary>
    /// 请求创建并进入主机房间
    /// </summary>
    public readonly struct CreateRoomRequestedEvent : IPresentationEvent { }

    /// <summary>
    /// 请求搜索并加入客户端房间
    /// </summary>
    public readonly struct JoinRoomRequestedEvent : IPresentationEvent { }
}
