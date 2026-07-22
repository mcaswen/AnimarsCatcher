using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    /// <summary>
    ///     包含单个离散的 NetworkConnection 连接或断开事件
    ///     更多信息请参阅 <see cref="NetworkStreamDriver.ConnectionEventsForTick"/>
    /// </summary>
    public struct NetCodeConnectionEvent
    {
        /// <summary>
        ///     触发此事件的客户端 <see cref="NetworkId" />
        /// </summary>
        public NetworkId Id;

        /// <summary>
        ///     此连接 Entity 的 <see cref="NetworkStreamConnection.Value"/> 值
        /// </summary>
        public NetworkConnection ConnectionId;

        /// <summary>
        ///     <see cref="ConnectionState.State" /> 的当前值
        /// </summary>
        /// <remarks>
        ///     每当此状态变化时都会触发事件，因此单个连接每帧可能发生多次状态变化
        /// </remarks>
        public ConnectionState.State State;

        /// <summary>
        ///     仅当 <see cref="State" /> 为 <see cref="ConnectionState.State.Disconnected" /> 时有效
        /// </summary>
        public NetworkStreamDisconnectReason DisconnectReason;

        /// <summary>
        ///     包含 <see cref="NetworkStreamConnection"/> 组件的 Entity
        /// </summary>
        public Entity ConnectionEntity;

        /// <summary>
        /// 返回便于阅读的值描述
        /// </summary>
        /// <returns>便于阅读的值描述</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString128Bytes ToFixedString()
        {
            FixedString128Bytes s = "NetCodeConnEvt[";
            s.Append(Id.ToFixedString());
            s.Append(',');
            s.Append(ConnectionId.ToFixedString());
            s.Append(',');
            s.Append(State.ToFixedString());
            if (DisconnectReason >= 0)
            {
                s.Append(',');
                s.Append(DisconnectReason.ToFixedString());
            }
            s.Append(']');
            return s;
        }

        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }
}
