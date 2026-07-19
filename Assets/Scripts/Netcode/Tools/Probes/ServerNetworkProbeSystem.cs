namespace AnimarsCatcher.Networking
{
    using Unity.Burst;
    using Unity.Entities;
    using Unity.NetCode;
    using UnityEngine;

    /// <summary>
    /// 按固定间隔输出 Server World 的连接阶段计数
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ServerNetworkProbeSystem : ISystem
    {
        private double _nextLogTime;

        public void OnUpdate(ref SystemState state)
        {
            if (SystemAPI.Time.ElapsedTime < _nextLogTime) return;
            _nextLogTime = SystemAPI.Time.ElapsedTime + 1;

            int connectionCount = 0, connectionWithIdCount = 0, inGameConnectionCount = 0;

            foreach (var _ in SystemAPI.Query<RefRO<NetworkStreamConnection>>()) connectionCount++;
            foreach (var _ in SystemAPI.Query<RefRO<NetworkId>>()) connectionWithIdCount++;
            foreach (var _ in SystemAPI.Query<RefRO<NetworkId>>().WithAll<NetworkStreamInGame>()) inGameConnectionCount++;

            Debug.Log($"[Server NetworkProbe] connectionCount = {connectionCount} connectionWithIdCount = {connectionWithIdCount} inGameConnectionCount = {inGameConnectionCount}");
        }
    }
}
