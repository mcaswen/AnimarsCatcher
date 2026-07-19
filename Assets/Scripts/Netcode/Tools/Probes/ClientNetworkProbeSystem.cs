namespace AnimarsCatcher.Networking
{
    using Unity.Burst;
    using Unity.Entities;
    using Unity.NetCode;
    using UnityEngine;

    /// <summary>
    /// 按固定间隔输出 Client World 的连接阶段状态
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ClientNetworkProbeSystem : ISystem
    {
        private double _nextLogTime;

        public void OnUpdate(ref SystemState state)
        {
            if (SystemAPI.Time.ElapsedTime < _nextLogTime) return;
            _nextLogTime = SystemAPI.Time.ElapsedTime + 1.0;

            bool hasConnection = !SystemAPI.QueryBuilder().WithAll<NetworkStreamConnection>().Build().IsEmpty;
            bool hasNetworkId = SystemAPI.HasSingleton<NetworkId>();
            bool hasInGameConnection = !SystemAPI.QueryBuilder().WithAll<NetworkId, NetworkStreamInGame>().Build().IsEmpty;

            Debug.Log($"[Client NetworkProbe] hasConnection = {hasConnection} hasNetworkId = {hasNetworkId} hasInGameConnection = {hasInGameConnection}");
        }
    }
}
